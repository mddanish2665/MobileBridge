using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MobileBridge.Services;

/// <summary>Creates and runs the virtual phone using Google's official Android Emulator.</summary>
public sealed class EmulatorService
{
    public const string AvdName = "MobileBridge";
    public const int ConsolePort = 5554;
    public const string Serial = "emulator-5554";

    Process? _proc;

    public static string AvdIni => Path.Combine(AppPaths.AvdHome, AvdName + ".ini");
    public static string AvdDir => Path.Combine(AppPaths.AvdHome, AvdName + ".avd");
    public static bool AvdExists => File.Exists(AvdIni) && File.Exists(Path.Combine(AvdDir, "config.ini"));

    static void ApplyEnv(IDictionary<string, string?> env)
    {
        env["ANDROID_HOME"] = AppPaths.Sdk;
        env["ANDROID_SDK_ROOT"] = AppPaths.Sdk;
        env["ANDROID_AVD_HOME"] = AppPaths.AvdHome;
    }

    public static async Task<(bool ok, string message)> CheckAccelerationAsync()
    {
        if (!File.Exists(AppPaths.EmulatorExe)) return (false, "The emulator is not downloaded yet.");
        try
        {
            var r = await ProcessRunner.RunAsync(AppPaths.EmulatorExe, new[] { "-accel-check" }, TimeSpan.FromSeconds(60), ApplyEnv);
            var text = string.Join(" ", r.All.Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != "accel:" && l != "accel" && !int.TryParse(l, out _)));
            return (r.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static int? InstalledAvdApi()
    {
        var cfg = Path.Combine(AvdDir, "config.ini");
        if (!File.Exists(AvdIni) || !File.Exists(cfg)) return null;
        var m = Regex.Match(File.ReadAllText(cfg), @"image\.sysdir\.1=system-images[\\/]android-(\d+)[\\/]");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Writes the AVD files directly (what avdmanager would do), so no Java is needed.</summary>
    public static void CreateAvd(int api)
    {
        Directory.CreateDirectory(AppPaths.AvdHome);
        if (Directory.Exists(AvdDir)) Directory.Delete(AvdDir, true);
        Directory.CreateDirectory(AvdDir);

        File.WriteAllText(AvdIni,
            "avd.ini.encoding=UTF-8\n" +
            $"path={AvdDir}\n" +
            $"path.rel=avd\\{AvdName}.avd\n" +
            $"target=android-{api}\n");

        long totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        int ramMb = totalMb >= 12000 ? 4096 : totalMb >= 7000 ? 3072 : 2048;
        int cores = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        var cfg = new (string key, string value)[]
        {
            ("AvdId", AvdName),
            ("PlayStore.enabled", "true"),
            ("abi.type", "x86_64"),
            ("avd.ini.displayname", "MobileBridge Phone"),
            ("avd.ini.encoding", "UTF-8"),
            ("disk.dataPartition.size", "8G"),
            ("fastboot.forceColdBoot", "no"),
            ("fastboot.forceFastBoot", "yes"),
            ("hw.accelerometer", "yes"),
            ("hw.audioInput", "yes"),
            ("hw.battery", "yes"),
            ("hw.camera.back", "virtualscene"),
            ("hw.camera.front", "emulated"),
            ("hw.cpu.arch", "x86_64"),
            ("hw.cpu.ncore", cores.ToString()),
            ("hw.dPad", "no"),
            ("hw.device.manufacturer", "Google"),
            ("hw.device.name", "pixel_6"),
            ("hw.gps", "yes"),
            ("hw.gpu.enabled", "yes"),
            ("hw.gpu.mode", "auto"),
            ("hw.initialOrientation", "portrait"),
            ("hw.keyboard", "yes"),
            ("hw.lcd.density", "420"),
            ("hw.lcd.height", "2400"),
            ("hw.lcd.width", "1080"),
            ("hw.mainKeys", "no"),
            ("hw.ramSize", ramMb.ToString()),
            ("hw.sdCard", "no"),
            ("hw.sensors.orientation", "yes"),
            ("hw.sensors.proximity", "yes"),
            ("hw.trackBall", "no"),
            ("image.sysdir.1", $"system-images\\android-{api}\\google_apis_playstore\\x86_64\\"),
            ("runtime.network.latency", "none"),
            ("runtime.network.speed", "full"),
            ("showDeviceFrame", "no"),
            ("skin.dynamic", "yes"),
            ("skin.name", "1080x2400"),
            ("skin.path", "_no_skin"),
            ("tag.display", "Google Play"),
            ("tag.id", "google_apis_playstore"),
            ("vm.heapSize", "512"),
        };
        File.WriteAllText(Path.Combine(AvdDir, "config.ini"),
            string.Join("\n", cfg.Select(c => $"{c.key}={c.value}")) + "\n");
        Log.Info($"Virtual phone created: Android API {api}, {ramMb} MB RAM, {cores} CPU cores.");
    }

    public static void DeleteAvd()
    {
        if (Directory.Exists(AvdDir)) Directory.Delete(AvdDir, true);
        if (File.Exists(AvdIni)) File.Delete(AvdIni);
    }

    public bool IsProcessRunning => _proc is { HasExited: false };

    public async Task<bool> IsRunningAsync()
    {
        if (IsProcessRunning) return true;
        if (!Adb.Installed) return false;
        try { return (await Adb.GetDevicesAsync()).Any(d => d.Serial == Serial); }
        catch { return false; }
    }

    public async Task StartAsync(bool coldBoot, bool wipeData, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        // adb must be running first: it creates the key the emulator installs into Android,
        // so the phone trusts this PC without a prompt.
        await Adb.StartServerAsync();
        Directory.CreateDirectory(AppPaths.Logs);
        ResetSavedWindowPosition();

        var args = new List<string>
        {
            "-avd", AvdName, "-port", ConsolePort.ToString(), "-no-boot-anim",
            "-netdelay", "none", "-netspeed", "full",
        };
        if (coldBoot || wipeData) args.Add("-no-snapshot-load");
        if (wipeData) args.Add("-wipe-data");

        // Launch through cmd so the emulator's output goes to a log file and the emulator keeps
        // running independently even if MobileBridge is closed.
        var cmd = $"/d /s /c \"\"{AppPaths.EmulatorExe}\" {string.Join(" ", args)} > \"{AppPaths.EmulatorLog}\" 2>&1\"";
        var psi = new ProcessStartInfo("cmd.exe", cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.EmulatorDir,
        };
        ApplyEnv(psi.Environment);
        Log.Info("Starting emulator: emulator " + string.Join(" ", args));
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the emulator.");

        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited)
                throw new InvalidOperationException("The emulator closed unexpectedly.\n\n" + TailEmulatorLog(12));
            progress.Report(new($"Starting Android… {(int)sw.Elapsed.TotalSeconds}s  (the first start can take 2–5 minutes)"));
            try
            {
                if (await Adb.IsBootCompletedAsync(Serial)) break;
            }
            catch { /* not up yet */ }
            if (sw.Elapsed > TimeSpan.FromMinutes(10))
                throw new TimeoutException("Android did not finish starting within 10 minutes. See the Log tab.\n\n" + TailEmulatorLog(8));
            await Task.Delay(2000, ct);
        }
        Log.Info($"Android started in {sw.Elapsed.TotalSeconds:0} s.");
        progress.Report(new("Android started. Preparing the phone window…"));
        await PreparePhoneAsync(bringToFront: true);
    }

    /// <summary>
    /// Wakes the phone and keeps its screen on (a sleeping phone shows as a black window),
    /// then makes sure the phone window is visible on screen.
    /// </summary>
    public static async Task PreparePhoneAsync(bool bringToFront)
    {
        string[][] commands =
        {
            new[] { "svc", "power", "stayon", "true" },
            new[] { "settings", "put", "system", "screen_off_timeout", "2147483647" },
            new[] { "input", "keyevent", "KEYCODE_WAKEUP" },
            new[] { "wm", "dismiss-keyguard" },
        };
        foreach (var c in commands)
        {
            try { await Adb.RunAsync(TimeSpan.FromSeconds(15), default, new[] { "-s", Serial, "shell" }.Concat(c).ToArray()); }
            catch (Exception ex) { Log.Info($"Could not run '{string.Join(' ', c)}': {ex.Message}"); }
        }
        // The window can appear a moment after Android reports it is ready.
        for (int i = 0; i < 10 && !WindowHelper.EnsurePhoneWindowVisible(bringToFront); i++)
            await Task.Delay(1000);
    }

    /// <summary>Removes a remembered window position that may be off-screen (e.g. after a monitor change).</summary>
    static void ResetSavedWindowPosition()
    {
        var userIni = Path.Combine(AvdDir, "emulator-user.ini");
        if (!File.Exists(userIni)) return;
        try
        {
            var lines = File.ReadAllLines(userIni)
                .Where(l => !l.StartsWith("window.x", StringComparison.Ordinal) && !l.StartsWith("window.y", StringComparison.Ordinal));
            File.WriteAllLines(userIni, lines);
        }
        catch { /* not critical */ }
    }

    public async Task StopAsync()
    {
        try { await Adb.RunAsync(TimeSpan.FromSeconds(20), default, "-s", Serial, "emu", "kill"); } catch { }
        for (int i = 0; i < 30; i++)
        {
            if (!await IsRunningAsync()) return;
            await Task.Delay(1000);
        }
        Log.Info("Emulator did not stop by itself, forcing it to close.");
        try { _proc?.Kill(true); } catch { }
        foreach (var name in new[] { "qemu-system-x86_64", "qemu-system-x86_64-headless", "emulator" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var file = p.MainModule?.FileName ?? "";
                    if (file.StartsWith(AppPaths.Sdk, StringComparison.OrdinalIgnoreCase)) p.Kill(true);
                }
                catch { }
            }
        }
    }

    public static string TailEmulatorLog(int lines)
    {
        try
        {
            using var fs = new FileStream(AppPaths.EmulatorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            return string.Join("\n", all.TakeLast(lines));
        }
        catch { return "(no emulator log)"; }
    }
}
