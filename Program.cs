using RuianFeedParser.Data;
using RuianFeedParser.Services;
using static RuianFeedParser.Services.LogLevel;

namespace RuianFeedParser;

internal static class Program
{
    private static readonly string DefaultDbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "RuianParser", "ruian.db");

    private static readonly string DefaultTempDir =
        Path.Combine(Path.GetTempPath(), "RuianParser");

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 0; }

        var command = args[0].ToLowerInvariant();
        var dbPath  = GetArg(args, "--db") ?? DefaultDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        if (GetFlag(args, "--verbose")) Logger.SetLevel(LogLevel.Debug);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[!] Cancelling...");
            cts.Cancel();
        };

        using var db = new Database(dbPath);
        using var downloader = new ThrottledDownloader(
            maxConcurrent: 2,
            delayBetweenRequestsMs: 400,
            timeoutSeconds: 600);

        var svc = new RuianImportService(db, downloader, DefaultTempDir);

        try
        {
            switch (command)
            {
                case "fetch-feed":    return await CmdFetchFeed(args, svc, cts.Token);
                case "import":        return await CmdImport(args, svc, cts.Token);
                case "import-ruian":  return await CmdImportRuian(args, svc, cts.Token);
                case "update-ruian":  return await CmdUpdateRuian(args, svc, cts.Token);
                case "view-feed":     return CmdViewFeed(args, db);
                case "view-addr":     return CmdViewAddr(args, db);
                case "stats":         return CmdStats(db);
                case "help":
                case "--help":        PrintHelp(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown command: {command}. Run 'help' for usage.");
                    return 1;
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("Cancelled."); return 1; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            if (GetFlag(args, "--verbose")) Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    static async Task<int> CmdFetchFeed(string[] args, RuianImportService svc, CancellationToken ct)
    {
        var url = GetPositional(args, 1) ?? throw new ArgumentException("Usage: fetch-feed <url>");
        await svc.FetchAndStoreFeedAsync(url, ct);
        return 0;
    }

    static async Task<int> CmdImport(string[] args, RuianImportService svc, CancellationToken ct)
    {
        var url   = GetPositional(args, 1) ?? throw new ArgumentException("Usage: import <url>");
        bool force = GetFlag(args, "--force");
        await svc.ImportCsvFeedAsync(url, force: force, ct: ct);
        return 0;
    }

    static async Task<int> CmdImportRuian(string[] args, RuianImportService svc, CancellationToken ct)
    {
        bool useState = GetFlag(args, "--state") || !GetFlag(args, "--municipalities");
        bool force    = GetFlag(args, "--force");
        var feedUrl   = useState
            ? RuianImportService.FeedCsvState
            : RuianImportService.FeedCsvMunicipality;

        Console.WriteLine(useState
            ? "RUIAN initial import — whole state CSV"
            : "RUIAN initial import — per municipality CSVs");

        string? municipalityFilter = GetArg(args, "--municipality");
        Func<RuianFeedParser.Models.FeedEntry, bool>? filter = null;

        if (municipalityFilter != null && !useState)
        {
            filter = e => e.Title.Contains(municipalityFilter, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"Filtering by municipality: {municipalityFilter}");
        }

        await svc.ImportCsvFeedAsync(feedUrl, filter, force, ct);
        return 0;
    }

    static async Task<int> CmdUpdateRuian(string[] args, RuianImportService svc, CancellationToken ct)
    {
        // --full uses the monthly VFR basic snapshot instead of daily deltas
        bool full   = GetFlag(args, "--full");
        bool force  = GetFlag(args, "--force");
        var feedUrl = full
            ? RuianImportService.FeedVfrFullBasic
            : RuianImportService.FeedVfrDailyDelta;

        // --days <n> limits how many delta files to apply (most recent n days)
        int? maxDays = null;
        var daysArg  = GetArg(args, "--days");
        if (daysArg != null && int.TryParse(daysArg, out var d)) maxDays = d;

        Console.WriteLine(full
            ? "RUIAN update — monthly VFR full basic snapshot"
            : $"RUIAN update — daily VFR deltas{(maxDays.HasValue ? $" (last {maxDays} days)" : "")}");
        Console.WriteLine("Applies upserts and deletions from RUIAN change feed.\n");

        await svc.UpdateVfrAsync(feedUrl, force, maxDays, ct);
        return 0;
    }

    static int CmdViewFeed(string[] args, Database db)
    {
        var feedUrl = GetArg(args, "--url");
        int limit   = int.TryParse(GetArg(args, "--limit"), out var l) ? l : 20;

        var entries = db.GetFeedEntries(feedUrl, limit);
        if (entries.Count == 0) { Console.WriteLine("No feed entries in database."); return 0; }

        Console.WriteLine($"\n{"Title",-50} {"Updated",-22} {"Size",10}  URL");
        Console.WriteLine(new string('-', 110));
        foreach (var e in entries)
        {
            var size    = e.FileSizeBytes.HasValue ? FormatBytes(e.FileSizeBytes.Value) : "-";
            var title   = Truncate(e.Title, 48);
            var updated = e.Updated == DateTime.MinValue ? "-" : e.Updated.ToString("yyyy-MM-dd HH:mm");
            Console.WriteLine($"{title,-50} {updated,-22} {size,10}  {e.DownloadUrl ?? e.AlternateUrl ?? "-"}");
        }
        Console.WriteLine($"\n({entries.Count} entries)");
        return 0;
    }

    static int CmdViewAddr(string[] args, Database db)
    {
        var municipality = GetArg(args, "--municipality") ?? GetArg(args, "-m");
        var street       = GetArg(args, "--street")       ?? GetArg(args, "-s");
        var postalCode   = GetArg(args, "--postal")       ?? GetArg(args, "-p");
        int limit        = int.TryParse(GetArg(args, "--limit"), out var l) ? l : 50;

        if (municipality == null && street == null && postalCode == null)
        {
            Console.Error.WriteLine("Specify at least one filter: --municipality, --street, or --postal");
            return 1;
        }

        var rows = db.QueryAddresses(municipality, street, postalCode, limit);
        if (rows.Count == 0) { Console.WriteLine("No matching addresses."); return 0; }

        Console.WriteLine($"\n{"ADM Code",-12} {"Municipality",-25} {"Part",-20} {"Street",-25} {"Number",-10} {"PSC",-8}");
        Console.WriteLine(new string('-', 110));
        foreach (var row in rows)
        {
            var number = row["house_number"]?.ToString() ?? "";
            if (row["orientation_number"] != null)
                number += "/" + row["orientation_number"] + (row["orientation_char"]?.ToString() ?? "");

            Console.WriteLine(
                $"{row["adm_code"],-12} " +
                $"{Truncate(row["municipality_name"]?.ToString() ?? "-", 23),-25} " +
                $"{Truncate(row["part_name"]?.ToString() ?? "-", 18),-20} " +
                $"{Truncate(row["street_name"]?.ToString() ?? "-", 23),-25} " +
                $"{number,-10} " +
                $"{row["postal_code"] ?? "-",-8}");
        }
        Console.WriteLine($"\n({rows.Count} addresses)");
        return 0;
    }

    static int CmdStats(Database db)
    {
        var (addresses, feeds, imports) = db.GetStats();
        Console.WriteLine($"\n  Addresses    : {addresses:N0}");
        Console.WriteLine($"  Feed entries : {feeds:N0}");
        Console.WriteLine($"  Imports done : {imports:N0}");
        return 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string? GetPositional(string[] args, int index)
        => index < args.Length && !args[index].StartsWith('-') ? args[index] : null;

    static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    static bool GetFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "~";

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:F1} {units[u]}";
    }

    static void PrintHelp() => Console.WriteLine(@"
RUIAN Feed Parser -- CUZK ATOM feed downloader, address database & incremental updater

COMMANDS:
  fetch-feed <url>
      Fetch any ATOM feed and store its entries in the DB. No downloads.

  import <url> [--force]
      CSV import pipeline: fetch feed -> download -> parse -> store.
      Skips entries already in import_log. --force re-downloads everything.

  import-ruian [--state | --municipalities] [--municipality <name>] [--force]
      Initial load from standard CUZK CSV feeds.
      --state            Whole-state CSV, ~150 MB compressed (default)
      --municipalities   Per-municipality CSVs
      --municipality     Filter by name (use with --municipalities)
      --force            Re-import even if already done

  update-ruian [--days <n>] [--full] [--force]
      Incremental update from VFR zmeny (change) feed.
      Downloads only delta files not yet in import_log.
      Upserts changed addresses. Deletes addresses removed from RUIAN.
      --days <n>    Apply only the last n daily delta files (default: all new)
      --full        Use monthly VFR full basic snapshot instead of daily deltas
      --force       Reapply already-processed delta files

  view-feed [--url <feed_url>] [--limit <n>]
      Display stored feed entries.

  view-addr --municipality <n> | --street <n> | --postal <code> [--limit <n>]
      Query stored addresses. At least one filter required.

  stats
      Show DB row counts.

GLOBAL OPTIONS:
  --db <path>     Custom SQLite file (default: %%LOCALAPPDATA%%\RuianParser\ruian.db)
  --verbose       Print stack traces on error

TYPICAL WORKFLOW:
  # One-time initial load
  ruian-parser import-ruian --state

  # Daily incremental updates (run from cron/task scheduler)
  ruian-parser update-ruian

  # Force reapply last 7 days of deltas
  ruian-parser update-ruian --days 7 --force

  # Works with any ATOM feed
  ruian-parser fetch-feed https://atom.cuzk.cz/RUIAN-S-ZA-Z/RUIAN-S-ZA-Z.xml
  ruian-parser import https://atom.cuzk.cz/RUIAN-OBCE-SHP/RUIAN-OBCE-SHP.xml
");
}
