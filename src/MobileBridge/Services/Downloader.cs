using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MobileBridge.Services;

public static class Downloader
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MobileBridge/1.0");
        return c;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await Http.GetStringAsync(url, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException($"Timed out contacting {new Uri(url).Host}. Check your internet connection."); }
        catch (HttpRequestException ex)
        { throw new IOException($"Could not reach {new Uri(url).Host}: {ex.Message}"); }
    }

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    /// <summary>Downloads with resume, automatic retry and optional SHA-1 verification.</summary>
    public static async Task DownloadFileAsync(string url, string dest, long expectedSize, string? sha1, string label,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
        {
            if (expectedSize <= 0 || new FileInfo(dest).Length == expectedSize) return;
            File.Delete(dest);
        }
        var part = dest + ".part";
        Log.Info($"Downloading {label}: {url}");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, part, expectedSize, label, progress, ct);
                break;
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is IOException) && attempt < 5 && !ct.IsCancellationRequested)
            {
                Log.Info($"Download interrupted ({ex.Message}). Retrying ({attempt}/4)â€¦");
                progress.Report(new($"Connection problem, retrying {label}â€¦"));
                await Task.Delay(2000 * attempt, ct);
            }
        }

        if (!string.IsNullOrEmpty(sha1))
        {
            progress.Report(new($"Verifying {label}â€¦"));
            var hash = await Task.Run(() =>
            {
                using var fs = File.OpenRead(part);
                return Convert.ToHexString(SHA1.HashData(fs));
            }, ct);
            if (!hash.Equals(sha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new IOException($"The download of {label} was corrupted (checksum mismatch). Please try again.");
            }
        }
        File.Move(part, dest, true);
    }

    static async Task DownloadOnceAsync(string url, string part, long expectedSize, string label,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize > 0 && existing == expectedSize) return;
        if (expectedSize > 0 && existing > expectedSize) { File.Delete(part); existing = 0; }

        using var stall = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        stall.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent) existing = 0;
            resp.EnsureSuccessStatusCode();

            long total = expectedSize > 0 ? expectedSize : existing + (resp.Content.Headers.ContentLength ?? 0);
            await using var src = await resp.Content.ReadAsStreamAsync(linked.Token);
            await using var fs = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 16, true);

            var buf = new byte[1 << 20];
            long done = existing, startDone = existing;
            var sw = Stopwatch.StartNew();
            long lastReport = -1000;
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                int n = await src.ReadAsync(buf, linked.Token);
                if (n == 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), linked.Token);
                done += n;
                if (sw.ElapsedMilliseconds - lastReport > 300)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    var speed = (long)((done - startDone) / Math.Max(0.5, sw.Elapsed.TotalSeconds));
                    progress.Report(new(
                        $"Downloading {label}â€¦ {FormatSize(done)} / {FormatSize(total)}  ({FormatSize(speed)}/s)",
                        total > 0 ? done * 100.0 / total : -1));
                }
            }
            if (expectedSize > 0 && done < expectedSize) throw new IOException("Connection closed early");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Connection stalled");
        }
    }
}

public static class Archive
{
    public static Task ExtractAsync(string zipPath, string destDir, string label, IProgress<ProgressInfo> progress,
        CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
        int i = 0;
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe path inside archive: " + e.FullName);
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
            done += e.Length;
            if (++i % 25 == 0)
                progress.Report(new($"Extracting {label}â€¦ {done * 100 / total}%", done * 100.0 / total));
        }
    }, ct);
}
