using System.Security.Cryptography;
using RuianFeedParser.Services;

namespace RuianFeedParser.Services;

/// <summary>
/// Throttled, retrying HTTP downloader.
///
/// Throttling  : SemaphoreSlim limits concurrent requests; delay between requests
///               prevents hammering the server.
/// Retry       : Exponential backoff on transient failures (network errors, 5xx, 429).
///               Non-retryable errors (4xx except 429) fail immediately.
/// Integrity   : SHA-256 of each downloaded file is returned for duplicate detection.
/// Streaming   : Files written in 80KB chunks — never fully in memory.
/// </summary>
public sealed class ThrottledDownloader : IDisposable
{
    private static readonly Logger Log = new("Downloader");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _delayBetweenRequests;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialBackoff;

    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly Lock _timeLock = new(); // System.Threading.Lock — C# 14 / net10

    public ThrottledDownloader(
        int maxConcurrent          = 2,
        int delayBetweenRequestsMs = 400,
        int timeoutSeconds         = 600,
        int maxRetries             = 5,
        int initialBackoffMs       = 2000)
    {
        _semaphore             = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _delayBetweenRequests  = TimeSpan.FromMilliseconds(delayBetweenRequestsMs);
        _maxRetries            = maxRetries;
        _initialBackoff        = TimeSpan.FromMilliseconds(initialBackoffMs);

        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer  = maxConcurrent,
            AutomaticDecompression   = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout           = TimeSpan.FromSeconds(30)
        });
        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "RuianFeedParser/2.0 (RUIAN address importer; contact your-email@example.com)");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fetch an ATOM feed XML. Returns the content as string.</summary>
    public async Task<string> FetchFeedAsync(string url, CancellationToken ct = default)
    {
        return await RetryAsync(async () =>
        {
            await EnterWithThrottleAsync(ct);
            try
            {
                Log.Debug($"GET {url}");
                var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                EnsureSuccess(response, url);
                return await response.Content.ReadAsStringAsync(ct);
            }
            finally { _semaphore.Release(); }
        }, url, ct);
    }

    /// <summary>
    /// Download a file to disk with streaming and progress reporting.
    /// Returns (localPath, sha256Hex) — the hash is used for duplicate detection.
    /// </summary>
    public async Task<(string path, string sha256)> DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await RetryAsync(async () =>
        {
            await EnterWithThrottleAsync(ct);
            try
            {
                Log.Debug($"GET {url} → {destinationPath}");
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                EnsureSuccess(response, url);

                var totalBytes = response.Content.Headers.ContentLength;
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                // Write to a .tmp file, rename on success — avoids leaving corrupt files
                var tmpPath = destinationPath + ".tmp";
                string sha256;

                await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var localStream  = new FileStream(tmpPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer    = new byte[81920];
                    long totalRead = 0;
                    int  read;

                    while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await localStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        hasher.AppendData(buffer, 0, read);
                        totalRead += read;
                        progress?.Report(new DownloadProgress(totalRead, totalBytes, url));

                        if (totalRead % (4 * 1024 * 1024) < read)
                            await Task.Yield();
                    }

                    sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }

                // Validate file integrity before committing
                ValidateDownloadedFile(tmpPath, url);

                File.Move(tmpPath, destinationPath, overwrite: true);
                Log.Debug($"Downloaded {destinationPath} sha256={sha256[..16]}…");
                return (destinationPath, sha256);
            }
            finally { _semaphore.Release(); }
        }, url, ct);
    }

    // ── Retry logic ───────────────────────────────────────────────────────────

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string url, CancellationToken ct)
    {
        var backoff = _initialBackoff;

        for (int attempt = 1; attempt <= _maxRetries + 1; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) { throw; }
            catch (NonRetryableException)      { throw; }
            catch (Exception ex) when (attempt <= _maxRetries)
            {
                Log.Warn($"Attempt {attempt}/{_maxRetries + 1} failed for {url}: {ex.Message}. " +
                         $"Retrying in {backoff.TotalSeconds:F0}s...");
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 60_000));
            }
        }

        // Last attempt — let exception propagate
        return await action();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EnsureSuccess(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;

        var status = (int)response.StatusCode;
        // 4xx (except 429 Too Many Requests) are not retryable
        if (status is >= 400 and < 500 and not 429)
            throw new NonRetryableException(
                $"HTTP {status} for {url} — not retrying (client error)");

        // 5xx and 429 are retryable
        response.EnsureSuccessStatusCode(); // throws HttpRequestException
    }

    private static void ValidateDownloadedFile(string path, string url)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new IOException($"Downloaded file is empty: {url}");

        // Check magic bytes for ZIP and GZip — the two formats ČÚZK uses
        Span<byte> magic = stackalloc byte[4];
        using var f = File.OpenRead(path);
        int read = f.Read(magic);

        if (read < 2) throw new IOException($"Downloaded file too small ({info.Length} bytes): {url}");

        bool isZip  = read >= 4 && magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;
        bool isGzip = magic[0] == 0x1F && magic[1] == 0x8B;
        bool isXml  = magic[0] == '<'; // plain XML

        if (!isZip && !isGzip && !isXml)
            throw new IOException(
                $"Downloaded file has unexpected format (magic bytes: {magic[0]:X2}{magic[1]:X2}): {url}");
    }

    private async Task EnterWithThrottleAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        TimeSpan delay;
        lock (_timeLock)
        {
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            delay = _delayBetweenRequests - elapsed;
            _lastRequestTime = DateTime.UtcNow + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }

    public void Dispose() { _http.Dispose(); _semaphore.Dispose(); }
}

public sealed class NonRetryableException(string message) : Exception(message);

public readonly struct DownloadProgress(long bytesReceived, long? totalBytes, string url)
{
    public long    BytesReceived { get; } = bytesReceived;
    public long?   TotalBytes    { get; } = totalBytes;
    public string  Url           { get; } = url;

    public double? Percent => TotalBytes is > 0
        ? (double)BytesReceived / TotalBytes.Value * 100 : null;

    public override string ToString()
    {
        static string Fmt(long b)
        {
            string[] u = ["B", "KB", "MB", "GB"];
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:F1} {u[i]}";
        }
        return TotalBytes.HasValue
            ? $"{Fmt(BytesReceived)} / {Fmt(TotalBytes.Value)} ({Percent:F1}%)"
            : $"{Fmt(BytesReceived)} downloaded";
    }
}
