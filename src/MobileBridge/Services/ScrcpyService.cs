using System.Diagnostics;
using System.Text.Json;

namespace MobileBridge.Services;

public sealed record MirrorOptions(int MaxSize, bool TurnScreenOff, bool Audio, bool StayAwake);

/// <summary>Mirrors and controls a real phone using scrcpy (open source, Apache-2.0).</summary>
public static class ScrcpyService
{
    public static string? ExePath =>
        Directory.Exists(AppPaths.ScrcpyDir)
            ? Directory.GetFiles(AppPaths.ScrcpyDir, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    public static async Task InstallAsync(IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        progress.Report(new("Finding the latest scrcpy release…"));
        var json = await Downloader.GetStringAsync("https://api.github.com/repos/Genymobile/scrcpy/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string? url = null;
        long size = 0;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("scrcpy-win64-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                break;
            }
        }
        if (url == null) throw new InvalidOperationException("Could not find the Windows download of scrcpy.");

        var zip = Path.Combine(AppPaths.Downloads, Path.GetFileName(new Uri(url).AbsolutePath));
        await Downloader.DownloadFileAsync(url, zip, size, null, $"scrcpy {tag}", progress, ct);
        if (Directory.Exists(AppPaths.ScrcpyDir)) Directory.Delete(AppPaths.ScrcpyDir, true);
        await Archive.ExtractAsync(zip, AppPaths.ScrcpyDir, "scrcpy", progress, ct);
        try { File.Delete(zip); } catch { }
        Log.Info($"Installed scrcpy {tag}.");
    }

    public static Process Start(AdbDevice device, MirrorOptions o)
    {
        var exe = ExePath ?? throw new InvalidOperationException("scrcpy is not installed. Click \"Download tools\" first.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        // Use our adb so scrcpy and MobileBridge share one adb server.
        psi.Environment["ADB"] = AppPaths.Adb;
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(device.Serial);
        psi.ArgumentList.Add("--window-title");
        psi.ArgumentList.Add($"{device.Model} — MobileBridge");
        if (o.MaxSize > 0) { psi.ArgumentList.Add("--max-size"); psi.ArgumentList.Add(o.MaxSize.ToString()); }
        if (o.TurnScreenOff) psi.ArgumentList.Add("--turn-screen-off");
        if (o.StayAwake) psi.ArgumentList.Add("--stay-awake");
        if (!o.Audio) psi.ArgumentList.Add("--no-audio");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("[scrcpy] " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("[scrcpy] " + e.Data); };
        Log.Info($"Starting mirroring for {device.Model} ({device.Serial}).");
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }
}
