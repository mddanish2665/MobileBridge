using System.Text.RegularExpressions;

namespace MobileBridge.Services;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.Ordinal);

    public string Display => State switch
    {
        "device" => $"{Model}   ({Serial})",
        "unauthorized" => $"{Serial}  —  unlock the phone and tap \"Allow\" on the USB debugging prompt",
        "offline" => $"{Serial}  —  offline (reconnect the cable)",
        _ => $"{Serial}  —  {State}",
    };
}

public static class Adb
{
    public static bool Installed => File.Exists(AppPaths.Adb);

    public static Task<ProcResult> RunAsync(TimeSpan timeout, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunAsync(AppPaths.Adb, args, timeout, null, ct);

    public static Task<ProcResult> RunAsync(params string[] args) =>
        RunAsync(TimeSpan.FromSeconds(30), default, args);

    public static async Task StartServerAsync()
    {
        if (!Installed) return;
        try { await RunAsync(TimeSpan.FromSeconds(30), default, "start-server"); }
        catch (Exception ex) { Log.Info("adb start-server failed: " + ex.Message); }
    }

    public static async Task<List<AdbDevice>> GetDevicesAsync()
    {
        var r = await RunAsync(TimeSpan.FromSeconds(15), default, "devices", "-l");
        var list = new List<AdbDevice>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("List of devices") || t.StartsWith('*')) continue;
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            list.Add(new AdbDevice(parts[0], parts[1], model));
        }
        return list;
    }

    public static async Task<bool> IsBootCompletedAsync(string serial)
    {
        var r = await RunAsync(TimeSpan.FromSeconds(10), default, "-s", serial, "shell", "getprop", "sys.boot_completed");
        return r.StdOut.Trim() == "1";
    }

    public static async Task<List<string>> GetUserPackagesAsync(string serial)
    {
        var r = await RunAsync(TimeSpan.FromSeconds(30), default, "-s", serial, "shell", "pm", "list", "packages", "-3");
        if (r.ExitCode != 0) throw new InvalidOperationException(r.All);
        return r.StdOut.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:"))
            .Select(l => l["package:".Length..])
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task LaunchAsync(string serial, string package)
    {
        var r = await RunAsync(TimeSpan.FromSeconds(30), default, "-s", serial, "shell", "monkey", "-p", package,
            "-c", "android.intent.category.LAUNCHER", "1");
        if (r.All.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{package} has no screen that can be opened from the launcher.");
    }

    public static async Task UninstallAsync(string serial, string package)
    {
        var r = await RunAsync(TimeSpan.FromMinutes(2), default, "-s", serial, "uninstall", package);
        if (!r.All.Contains("Success")) throw new InvalidOperationException("Uninstall failed: " + r.All);
    }
}

public static class ApkInstaller
{
    public static readonly string[] Extensions = { ".apk", ".xapk", ".apks" };
    static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    public static async Task InstallAsync(string serial, IReadOnlyList<string> files, IProgress<ProgressInfo> progress,
        CancellationToken ct)
    {
        var failures = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var name = Path.GetFileName(f);
            progress.Report(new($"Installing {name} ({i + 1}/{files.Count})…"));
            try
            {
                switch (Path.GetExtension(f).ToLowerInvariant())
                {
                    case ".apk":
                        Check(await Adb.RunAsync(InstallTimeout, ct, "-s", serial, "install", "-r", f));
                        break;
                    case ".xapk":
                    case ".apks":
                        await InstallBundleAsync(serial, f, progress, ct);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported file type. Use .apk, .xapk or .apks files.");
                }
                Log.Info($"Installed {name}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info($"Install failed for {name}: {ex.Message}");
                failures.Add($"{name}:\n{ex.Message}");
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join("\n\n", failures));
    }

    static void Check(ProcResult r)
    {
        if (!r.All.Contains("Success")) throw new InvalidOperationException(Explain(r.All));
    }

    static string Explain(string output)
    {
        var code = Regex.Match(output, @"INSTALL_[A-Z_]+").Value;
        var hint = code switch
        {
            "INSTALL_FAILED_NO_MATCHING_ABIS" => "This app does not contain code for this phone's processor. Use Real Phone mode, or get a 'universal' / x86_64 version of the APK.",
            "INSTALL_FAILED_UPDATE_INCOMPATIBLE" => "A version of this app signed by someone else is already installed. Uninstall it first.",
            "INSTALL_FAILED_VERSION_DOWNGRADE" => "A newer version of this app is already installed. Uninstall it first.",
            "INSTALL_FAILED_OLDER_SDK" => "This app needs a newer Android version than this phone has.",
            "INSTALL_FAILED_MISSING_SPLIT" => "This app is split into several files. Install the complete .xapk / .apks bundle instead of a single .apk.",
            "INSTALL_FAILED_INSUFFICIENT_STORAGE" => "Not enough free storage on the phone.",
            "INSTALL_PARSE_FAILED_NOT_APK" or "INSTALL_PARSE_FAILED_NO_CERTIFICATES" or "INSTALL_FAILED_INVALID_APK"
                => "The file is not a valid APK (it may be damaged or incomplete). Download it again.",
            "INSTALL_FAILED_USER_RESTRICTED" => "The phone blocked the install. On the phone, allow 'Install via USB' in Developer options and accept the prompt.",
            _ => "Android refused to install this app.",
        };
        var detail = output.Trim();
        if (detail.Length > 400) detail = detail[^400..];
        return $"{hint}\n({detail})";
    }

    static async Task InstallBundleAsync(string serial, string path, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var tmp = Path.Combine(AppPaths.Temp, "bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Archive.ExtractAsync(path, tmp, Path.GetFileName(path), progress, ct);
            var all = Directory.GetFiles(tmp, "*.apk", SearchOption.AllDirectories).ToList();
            var splitsDir = Path.Combine(tmp, "splits");
            var sep = Path.DirectorySeparatorChar;
            var apks = Directory.Exists(splitsDir)
                ? Directory.GetFiles(splitsDir, "*.apk").ToList()
                : all.Where(a => !a.Contains($"{sep}standalones{sep}")).ToList();
            if (apks.Count == 0) apks = all;
            if (apks.Count == 0) throw new InvalidOperationException("No APK files were found inside this bundle.");

            progress.Report(new($"Installing {Path.GetFileName(path)} ({apks.Count} parts)…"));
            var args = apks.Count == 1
                ? new[] { "-s", serial, "install", "-r", apks[0] }
                : new[] { "-s", serial, "install-multiple", "-r" }.Concat(apks).ToArray();
            Check(await Adb.RunAsync(InstallTimeout, ct, args));

            // XAPK files may carry game data (OBB) that must be copied to the phone.
            var obbRoot = Path.Combine(tmp, "Android", "obb");
            if (Directory.Exists(obbRoot))
            {
                foreach (var pkgDir in Directory.GetDirectories(obbRoot))
                {
                    progress.Report(new("Copying game data (OBB) to the phone…"));
                    var r = await Adb.RunAsync(TimeSpan.FromMinutes(60), ct, "-s", serial, "push", pkgDir, "/sdcard/Android/obb/");
                    if (r.ExitCode != 0) throw new InvalidOperationException("The app was installed, but copying its game data failed:\n" + r.All);
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }
}
