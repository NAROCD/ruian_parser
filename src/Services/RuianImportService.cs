using RuianFeedParser.Data;
using RuianFeedParser.Models;
using RuianFeedParser.Parsers;
using System.IO.Compression;

namespace RuianFeedParser.Services;

public sealed class RuianImportService
{
    public const string FeedCsvState        = "https://atom.cuzk.cz/RUIAN-CSV-ADR-ST/RUIAN-CSV-ADR-ST.xml";
    public const string FeedCsvMunicipality = "https://atom.cuzk.cz/RUIAN-CSV-ADR-OB/RUIAN-CSV-ADR-OB.xml";
    public const string FeedVfrDailyDelta   = "https://atom.cuzk.cz/RUIAN-S-ZA-Z/RUIAN-S-ZA-Z.xml";
    public const string FeedVfrFullBasic    = "https://atom.cuzk.cz/RUIAN-S-ZA-U/RUIAN-S-ZA-U.xml";

    private static readonly Logger Log = new("ImportService");

    private readonly Database _db;
    private readonly ThrottledDownloader _downloader;
    private readonly string _tempDir;

    public RuianImportService(Database db, ThrottledDownloader downloader, string tempDir)
    {
        _db = db;
        _downloader = downloader;
        _tempDir = tempDir;
        Directory.CreateDirectory(tempDir);
    }

    // ── Feed fetch ────────────────────────────────────────────────────────────

    public async Task<AtomFeed> FetchAndStoreFeedAsync(string feedUrl, CancellationToken ct = default)
    {
        Log.Info($"Fetching feed: {feedUrl}");
        var xml = await _downloader.FetchFeedAsync(feedUrl, ct);
        var feed = AtomFeedParser.ParseFromString(xml, feedUrl);
        int stored = _db.UpsertFeedEntries(feed.Entries, feedUrl);
        Log.Info($"Feed: {feed.Entries.Count} entries, {stored} upserted. Title: {feed.Title}");
        return feed;
    }

    // ── Sub-feed resolution (INSPIRE two-level ATOM) ─────────────────────────

    /// <summary>
    /// For entries that point to a dataset sub-feed (SubFeedUrl set), fetch the sub-feed
    /// and return an updated FeedEntry with the real DownloadUrl populated.
    /// Entries that already have a DownloadUrl are returned unchanged.
    /// </summary>
    private async Task<FeedEntry> ResolveSubFeedAsync(FeedEntry entry, CancellationToken ct)
    {
        if (entry.DownloadUrl != null || entry.SubFeedUrl == null)
            return entry;

        Log.Debug($"Resolving sub-feed: {entry.SubFeedUrl}");
        var xml     = await _downloader.FetchFeedAsync(entry.SubFeedUrl, ct);
        var subFeed = AtomFeedParser.ParseFromString(xml, entry.SubFeedUrl);

        // The sub-feed should have exactly one entry with the actual download link
        var dataEntry = subFeed.Entries.FirstOrDefault(e => e.DownloadUrl != null);
        if (dataEntry == null)
        {
            Log.Warn($"Sub-feed returned no downloadable entries: {entry.SubFeedUrl}");
            return entry;
        }

        Log.Debug($"Resolved download URL: {dataEntry.DownloadUrl}");

        // Merge: keep the top-level entry's metadata, use sub-feed entry's download details
        return entry with
        {
            DownloadUrl   = dataEntry.DownloadUrl,
            FileSizeBytes = dataEntry.FileSizeBytes ?? entry.FileSizeBytes,
            MediaType     = dataEntry.MediaType     ?? entry.MediaType,
            Updated       = dataEntry.Updated != DateTime.MinValue ? dataEntry.Updated : entry.Updated
        };
    }

    // ── Initial CSV import ────────────────────────────────────────────────────

    public async Task ImportCsvFeedAsync(
        string feedUrl,
        Func<FeedEntry, bool>? entryFilter = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var feed = await FetchAndStoreFeedAsync(feedUrl, ct);

        // Resolve sub-feeds first (INSPIRE two-level ATOM — ČÚZK uses this on all dataset feeds)
        var candidates = feed.Entries
            .Where(e => entryFilter == null || entryFilter(e))
            .ToList();

        Log.Info($"Resolving {candidates.Count} feed entries (may fetch sub-feeds)…");
        var resolved = new List<FeedEntry>(candidates.Count);
        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            resolved.Add(await ResolveSubFeedAsync(entry, ct));
        }

        var toDownload = resolved
            .Where(e => e.DownloadUrl != null)
            .ToList();

        Log.Info($"{toDownload.Count} downloadable entries found.");

        int skipped = 0, processed = 0;

        foreach (var entry in toDownload)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = FileNameFromUrl(entry.DownloadUrl!);

            // Pre-check by filename only — we don't have the hash yet
            if (!force && _db.IsAlreadyImported(fileName))
            {
                Log.Debug($"Skipping {fileName} (already imported by filename)");
                skipped++;
                continue;
            }

            await ImportCsvEntryAsync(entry, feedUrl, force, ct);
            processed++;
        }

        Log.Info($"CSV import done: {processed} processed, {skipped} skipped.");
        if (skipped > 0) Log.Info("Use --force to re-import skipped files.");
    }

    // ── VFR incremental update ────────────────────────────────────────────────

    public async Task UpdateVfrAsync(
        string feedUrl,
        bool force = false,
        int? maxDays = null,
        CancellationToken ct = default)
    {
        var feed = await FetchAndStoreFeedAsync(feedUrl, ct);

        // Apply oldest-first — same address can change multiple times in one feed window
        // Resolve sub-feeds (ČÚZK VFR feeds also use two-level ATOM)
        var candidates = feed.Entries.OrderBy(e => e.Updated).ToList();
        if (maxDays.HasValue) candidates = candidates.TakeLast(maxDays.Value).ToList();

        Log.Info($"Resolving {candidates.Count} VFR feed entries…");
        var entries = new List<FeedEntry>(candidates.Count);
        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await ResolveSubFeedAsync(entry, ct));
        }
        entries = entries.Where(e => e.DownloadUrl != null).ToList();

        Log.Info($"{entries.Count} VFR entries to consider.");

        int skipped = 0, processed = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = FileNameFromUrl(entry.DownloadUrl!);

            if (!force && _db.IsAlreadyImported(fileName))
            {
                Log.Debug($"Skipping {fileName} (already applied)");
                skipped++;
                continue;
            }

            await ApplyVfrEntryAsync(entry, feedUrl, ct);
            processed++;
        }

        Log.Info($"VFR update done: {processed} applied, {skipped} skipped.");
        if (skipped > 0) Log.Info("Use --force to reapply already-processed deltas.");
    }

    // ── Private: CSV entry ────────────────────────────────────────────────────

    private async Task ImportCsvEntryAsync(
        FeedEntry entry, string feedUrl, bool force, CancellationToken ct)
    {
        var url      = entry.DownloadUrl!;
        var fileName = FileNameFromUrl(url);
        var tmpPath  = Path.Combine(_tempDir, fileName);

        LogEntryHeader(entry, fileName);
        long importId = _db.StartImport(fileName, feedUrl);

        try
        {
            var progress = MakeProgress();
            var (localPath, sha256) = await _downloader.DownloadFileAsync(url, tmpPath, progress, ct);
            Log.Info($"Downloaded {fileName} — sha256={sha256[..16]}…");

            // Post-download hash check — if hash already imported and not forced, skip processing
            if (!force && _db.IsAlreadyImported(fileName, sha256))
            {
                Log.Info($"Skipping {fileName}: identical file already imported (hash match).");
                _db.FinishImport(importId, 0, status: "skipped-duplicate");
                return;
            }

            int rows = ProcessCsvFile(localPath, fileName);
            _db.FinishImport(importId, rows);
            Log.Info($"{fileName}: imported {rows:N0} addresses.");
        }
        catch (OperationCanceledException)
        {
            _db.FinishImport(importId, 0, status: "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _db.FinishImport(importId, 0, status: $"error");
            Log.Error($"{fileName}: import failed", ex);
        }
        finally { TryDelete(tmpPath); TryDelete(tmpPath + ".tmp"); }
    }

    private int ProcessCsvFile(string localPath, string fileName)
    {
        var ext = Path.GetExtension(localPath).ToLowerInvariant();
        if (ext == ".zip")
        {
            int total = 0;
            using var zip = ZipFile.OpenRead(localPath);
            foreach (var zipEntry in zip.Entries)
            {
                if (!zipEntry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                Log.Info($"  Parsing {zipEntry.Name} ({FormatBytes(zipEntry.Length)})");
                using var stream = zipEntry.Open();
                total += InsertCsvStream(stream, $"{fileName}/{zipEntry.Name}");
            }
            return total;
        }

        using var fs = File.OpenRead(localPath);
        return InsertCsvStream(fs, fileName);
    }

    private int InsertCsvStream(System.IO.Stream stream, string label)
    {
        var addresses = RuianCsvParser.ParseStream(stream, label);
        int last = 0;
        int count = _db.BulkInsertAddresses(addresses, progress: n =>
        {
            if (n - last >= 50_000)
            {
                Log.Progress($"{n:N0} rows inserted…");
                last = n;
            }
        });
        Log.Progress($"{count:N0} rows inserted.", done: true);
        return count;
    }

    // ── Private: VFR entry ────────────────────────────────────────────────────

    private async Task ApplyVfrEntryAsync(FeedEntry entry, string feedUrl, CancellationToken ct)
    {
        var url      = entry.DownloadUrl!;
        var fileName = FileNameFromUrl(url);
        var tmpPath  = Path.Combine(_tempDir, fileName);

        LogEntryHeader(entry, fileName);
        long importId = _db.StartImport(fileName, feedUrl);

        try
        {
            var progress = MakeProgress();
            var (localPath, sha256) = await _downloader.DownloadFileAsync(url, tmpPath, progress, ct);
            Log.Info($"Downloaded {fileName} — sha256={sha256[..16]}…");

            var (upserted, deleted) = ApplyVfrFile(localPath, fileName);
            _db.FinishImport(importId, upserted, deleted);
            Log.Info($"{fileName}: {upserted:N0} upserted, {deleted:N0} deleted.");
        }
        catch (OperationCanceledException)
        {
            _db.FinishImport(importId, 0, status: "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _db.FinishImport(importId, 0, status: "error");
            Log.Error($"{fileName}: VFR apply failed", ex);
        }
        finally { TryDelete(tmpPath); TryDelete(tmpPath + ".tmp"); }
    }

    private (int upserted, int deleted) ApplyVfrFile(string localPath, string fileName)
    {
        var ext = Path.GetExtension(localPath).ToLowerInvariant();

        List<VfrAddressRecord> records;
        if (ext == ".zip")
        {
            using var zipStream = File.OpenRead(localPath);
            records = VfrParser.ParseZip(zipStream, fileName);
        }
        else
        {
            using var stream = File.OpenRead(localPath);
            records = VfrParser.ParseStream(stream, fileName).ToList();
        }

        Log.Info($"  Parsed {records.Count} VFR records, applying…");

        int lastU = 0, lastD = 0;
        return _db.ApplyVfrChanges(records, progress: (u, d) =>
        {
            if (u - lastU >= 10_000 || d - lastD >= 1_000)
            {
                Log.Progress($"{u:N0} upserted, {d:N0} deleted…");
                lastU = u; lastD = d;
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Progress<DownloadProgress> MakeProgress()
    {
        var log = new Logger("Downloader");
        return new Progress<DownloadProgress>(p => log.Progress(p.ToString()));
    }

    private static void LogEntryHeader(FeedEntry entry, string fileName)
    {
        var size    = entry.FileSizeBytes.HasValue ? $" ({FormatBytes(entry.FileSizeBytes.Value)})" : "";
        var updated = entry.Updated != DateTime.MinValue ? $" [{entry.Updated:yyyy-MM-dd}]" : "";
        Log.Info($"Processing: {fileName}{size}{updated}");
    }

    private static string FileNameFromUrl(string url)
        => Path.GetFileName(new Uri(url).LocalPath);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { new Logger("ImportService").Warn($"Could not delete temp file {path}: {ex.Message}"); }
    }

    public static string FormatBytes(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {u[i]}";
    }
}
