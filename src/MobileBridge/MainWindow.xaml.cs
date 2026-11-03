using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MobileBridge.Services;

namespace MobileBridge;

public sealed record AndroidVersionOption(int Api, string Display);

public partial class MainWindow : Window, IBusyHost
{
    static readonly AndroidVersionOption[] Versions =
    {
        new(36, "Android 16 (API 36)"),
        new(35, "Android 15 (API 35)"),
        new(34, "Android 14 (API 34) — recommended"),
        new(33, "Android 13 (API 33)"),
        new(31, "Android 12 (API 31)"),
        new(30, "Android 11 (API 30)"),
    };

    const string AccelHelp =
        "The Android emulator needs hardware virtualization:\n" +
        "1. Turn on Virtualization (Intel VT-x / AMD-V / SVM) in your PC's BIOS/UEFI settings.\n" +
        "2. Click \"Enable Windows Hypervisor Platform…\" and restart the PC.";

    readonly EmulatorService _emu = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    AndroidRepository? _repo;
    CancellationTokenSource? _cts;
    bool _busy, _ticking, _emuOnline;
    bool? _accelOk;

    public MainWindow()
    {
        InitializeComponent();
        Log.Line += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        AndroidVersionBox.ItemsSource = Versions;
        AndroidVersionBox.SelectedItem = Versions.FirstOrDefault(v => v.Api == AppSettings.Current.AndroidApi) ?? Versions[2];
        AndroidVersionBox.SelectionChanged += (_, _) =>
        {
            AppSettings.Current.AndroidApi = SelectedApi;
            AppSettings.Current.Save();
            RefreshSetupStatus();
        };

        VirtualApps.Host = this;
        VirtualApps.SerialProvider = () => _emuOnline ? EmulatorService.Serial : null;
        VirtualApps.NoDeviceMessage = "Start the virtual phone first (Step 2 — Start Phone).";

        RealApps.Host = this;
        RealApps.SerialProvider = () => DevicesList.SelectedItem is AdbDevice { State: "device" } d ? d.Serial : null;
        RealApps.NoDeviceMessage = "Connect your phone and select it in the \"Detected phones\" list first.";
        DevicesList.SelectionChanged += async (_, _) => await RealApps.RefreshAsync(true);

        DataFolderBox.Text = AppPaths.Root;
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    int SelectedApi => (AndroidVersionBox.SelectedItem as AndroidVersionOption)?.Api ?? 34;

    async Task OnLoadedAsync()
    {
        Log.Info($"MobileBridge started. Data folder: {AppPaths.Root}");
        RefreshSetupStatus();
        await Adb.StartServerAsync();
        if (File.Exists(AppPaths.EmulatorExe)) await RunAccelCheckAsync();
        _timer.Start();
        await TickAsync();
    }

    // ------------------------------------------------------------------ busy / progress

    public async Task RunBusy(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work, bool cancellable = false)
    {
        if (_busy)
        {
            MessageBox.Show(this, "Another task is still running. Please wait for it to finish (see the status bar).",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _busy = true;
        _cts = new CancellationTokenSource();
        StatusText.Text = title + "…";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Log.Info(title);

        var progress = new Progress<ProgressInfo>(p =>
        {
            if (!_busy) return;
            StatusText.Text = p.Text;
            Progress.IsIndeterminate = p.Percent < 0;
            if (p.Percent >= 0) Progress.Value = p.Percent;
        });

        string final = "Ready.";
        try
        {
            await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            final = "Cancelled.";
            Log.Info(title + ": cancelled.");
        }
        catch (Exception ex)
        {
            final = "Error: " + ex.Message.Split('\n')[0];
            Log.Info($"{title} failed: {ex}");
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
            Progress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            StatusText.Text = final;
            RefreshSetupStatus();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ------------------------------------------------------------------ status

    static string Mark(bool ok) => ok ? "✅" : "⬜";

    bool IsSetupComplete(int api) =>
        Adb.Installed && File.Exists(AppPaths.EmulatorExe) && SdkInstaller.SystemImageInstalled(api) &&
        EmulatorService.InstalledAvdApi() == api;

    void RefreshSetupStatus()
    {
        int api = SelectedApi;
        bool pt = Adb.Installed;
        bool em = File.Exists(AppPaths.EmulatorExe);
        bool img = SdkInstaller.SystemImageInstalled(api);
        bool avd = EmulatorService.InstalledAvdApi() == api;
        SetupStatusText.Text =
            $"{Mark(pt)}  Platform tools (adb)\n" +
            $"{Mark(em)}  Android Emulator\n" +
            $"{Mark(img)}  Android system image with Google Play (API {api})\n" +
            $"{Mark(avd)}  Virtual phone created";
        SetupButton.Content = pt && em && img && avd ? "Check / Repair" : "Download & Set Up";

        bool scr = ScrcpyService.ExePath != null;
        MirrorSetupText.Text = $"{Mark(pt)}  Platform tools (adb)\n{Mark(scr)}  scrcpy (screen mirroring)";
        MirrorSetupButton.Content = pt && scr ? "Update tools" : "Download tools";
    }

    async Task TickAsync()
    {
        if (_ticking || !Adb.Installed) return;
        _ticking = true;
        try
        {
            var devices = await Adb.GetDevicesAsync();

            var emu = devices.FirstOrDefault(d => d.Serial == EmulatorService.Serial);
            bool online = emu?.State == "device";
            if (!_busy && online != _emuOnline)
            {
                _emuOnline = online;
                if (online)
                {
                    await EmulatorService.PreparePhoneAsync(bringToFront: false);
                    await VirtualApps.RefreshAsync(true);
                }
                else VirtualApps.Clear();
            }
            if (!_busy)
                EmuStatusText.Text = _emuOnline ? "Phone: running ✅" : emu != null ? "Phone: starting…" : "Phone: stopped";

            var real = devices.Where(d => !d.IsEmulator).ToList();
            var current = DevicesList.Items.Cast<AdbDevice>().ToList();
            if (!real.SequenceEqual(current))
            {
                var selected = (DevicesList.SelectedItem as AdbDevice)?.Serial;
                DevicesList.ItemsSource = real;
                DevicesList.SelectedItem = real.FirstOrDefault(d => d.Serial == selected)
                                           ?? real.FirstOrDefault(d => d.State == "device")
                                           ?? real.FirstOrDefault();
            }
            NoDevicesText.Visibility = real.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _ticking = false;
        }
    }

    // ------------------------------------------------------------------ licenses / disk

    bool EnsureLicenses(IEnumerable<RemotePackage> packages)
    {
        var ids = packages.Select(p => p.LicenseId).OfType<string>()
            .Where(id => !AppSettings.Current.AcceptedLicenses.Contains(id)).Distinct().ToList();
        if (ids.Count == 0) return true;
        var text = string.Join("\n\n──────────────────────────────\n\n",
            ids.Select(id => _repo != null && _repo.Licenses.TryGetValue(id, out var t) ? t : $"License: {id}"));
        var dlg = new LicenseWindow(text) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            Log.Info("License declined; nothing was downloaded.");
            return false;
        }
        AppSettings.Current.AcceptedLicenses.AddRange(ids);
        AppSettings.Current.Save();
        return true;
    }

    bool CheckDiskSpace(long needed)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.Root))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= needed) return true;
            return MessageBox.Show(this,
                $"Drive {root} has only {Downloader.FormatSize(free)} free; about {Downloader.FormatSize(needed)} is recommended.\n\n" +
                "You can choose a different storage folder in the Settings tab.\n\nContinue anyway?",
                "Low disk space", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ virtual phone

    async void Setup_Click(object sender, RoutedEventArgs e) => await RunBusy("Setting up the virtual phone", async (p, ct) =>
    {
        int api = SelectedApi;
        var existingApi = EmulatorService.InstalledAvdApi();
        if (existingApi != null && existingApi != api &&
            MessageBox.Show(this,
                $"Switching to Android API {api} creates a new virtual phone.\nApps and data on the current phone (API {existingApi}) will be deleted.\n\nContinue?",
                "Change Android version", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        p.Report(new("Getting the package list from Google…"));
        _repo ??= await AndroidRepository.LoadAsync(ct);

        var needed = new List<(RemotePackage pkg, string dir, string label)>();
        if (!Adb.Installed) needed.Add((_repo.PlatformTools, AppPaths.PlatformTools, "platform tools"));
        if (!File.Exists(AppPaths.EmulatorExe)) needed.Add((_repo.Emulator, AppPaths.EmulatorDir, "Android Emulator"));
        if (!SdkInstaller.SystemImageInstalled(api)) needed.Add((_repo.PlayStoreImage(api), AppPaths.SystemImageDir(api), $"Android {api} system image"));

        if (needed.Count > 0)
        {
            if (!EnsureLicenses(needed.Select(n => n.pkg))) return;
            long total = needed.Sum(n => n.pkg.Size);
            Log.Info($"Need to download {needed.Count} component(s), {Downloader.FormatSize(total)}.");
            if (!CheckDiskSpace(total * 2 + 8L * 1024 * 1024 * 1024)) return;
            foreach (var n in needed)
            {
                await SdkInstaller.InstallZipPackageAsync(n.pkg, n.dir, n.label, p, ct);
                RefreshSetupStatus();
            }
        }
        Directory.CreateDirectory(Path.Combine(AppPaths.Sdk, "platforms"));

        if (EmulatorService.InstalledAvdApi() != api)
        {
            if (await _emu.IsRunningAsync())
            {
                p.Report(new("Stopping the running phone…"));
                await _emu.StopAsync();
            }
            EmulatorService.CreateAvd(api);
        }

        await Adb.StartServerAsync();
        p.Report(new("Checking hardware acceleration…"));
        await RunAccelCheckAsync();
        RefreshSetupStatus();
        MessageBox.Show(this,
            _accelOk == true
                ? "Setup complete! Click \"Start Phone\" to launch your virtual Android phone."
                : "Setup complete, but hardware acceleration is not available yet, so the phone cannot start.\n\n" + AccelHelp,
            "MobileBridge", MessageBoxButton.OK, _accelOk == true ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }, cancellable: true);

    async Task RunAccelCheckAsync()
    {
        var (ok, msg) = await EmulatorService.CheckAccelerationAsync();
        _accelOk = File.Exists(AppPaths.EmulatorExe) ? ok : null;
        AccelText.Text = !File.Exists(AppPaths.EmulatorExe)
            ? "Checked automatically after setup."
            : ok ? $"✅ Ready. {msg}" : $"❌ Not available. {msg}\n\n{AccelHelp}";
        Log.Info("Acceleration check: " + msg);
    }

    async void AccelCheck_Click(object sender, RoutedEventArgs e) =>
        await RunBusy("Checking hardware acceleration", async (_, _) => await RunAccelCheckAsync());

    async void EnableHypervisor_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "This turns on the Windows feature \"Windows Hypervisor Platform\", which the Android emulator needs.\n\n" +
                "Windows will ask for administrator permission, and you must restart the PC afterwards.\n\nContinue?",
                "Enable Windows Hypervisor Platform", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("dism.exe",
                "/online /enable-feature /featurename:HypervisorPlatform /all /norestart")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc == null) return;
            await proc.WaitForExitAsync();
            Log.Info($"DISM finished with exit code {proc.ExitCode}.");
            MessageBox.Show(this,
                proc.ExitCode is 0 or 3010
                    ? "Windows Hypervisor Platform is turned on.\n\nPlease restart your PC, then open MobileBridge again."
                    : $"Windows reported error code {proc.ExitCode}. You can also turn it on manually: Start › \"Turn Windows features on or off\" › tick \"Windows Hypervisor Platform\".",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception)
        {
            // user pressed "No" on the administrator prompt
        }
    }

    async void StartEmu_Click(object sender, RoutedEventArgs e) => await StartPhoneAsync(wipe: false);

    async Task StartPhoneAsync(bool wipe) => await RunBusy(wipe ? "Factory resetting the virtual phone" : "Starting the virtual phone", async (p, ct) =>
    {
        int api = SelectedApi;
        if (!IsSetupComplete(api))
        {
            MessageBox.Show(this, "Please finish Step 1 first: click \"Download & Set Up\".", "MobileBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (await _emu.IsRunningAsync())
        {
            if (!wipe)
            {
                p.Report(new("The phone is already running — waiting for Android…"));
                var sw = Stopwatch.StartNew();
                while (!await SafeBootCompleted() && sw.Elapsed < TimeSpan.FromMinutes(5)) await Task.Delay(2000, ct);
                await EmulatorService.PreparePhoneAsync(bringToFront: true);
                await MarkOnlineAsync();
                return;
            }
            p.Report(new("Stopping the phone…"));
            await _emu.StopAsync();
        }
        if (_accelOk == null) await RunAccelCheckAsync();
        if (_accelOk == false &&
            MessageBox.Show(this, "Hardware acceleration is not available, so the phone will most likely fail to start.\n\n" + AccelHelp + "\n\nTry anyway?",
                "MobileBridge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        EmuStatusText.Text = "Phone: starting…";
        await _emu.StartAsync(ColdBootBox.IsChecked == true, wipe, p, ct);
        await MarkOnlineAsync();

        // On a brand-new phone Google Play Services updates itself in the background for a few
        // minutes; Google sign-in hangs on "Checking info…" until that update is done.
        var firstBootMarker = Path.Combine(EmulatorService.AvdDir, ".mobilebridge-first-boot-done");
        if (wipe || !File.Exists(firstBootMarker))
        {
            File.WriteAllText(firstBootMarker, DateTime.Now.ToString("O"));
            MessageBox.Show(this,
                "Your virtual phone is ready!\n\n" +
                "Important for the first start: Google Play Services is now updating itself in the background. " +
                "Please wait about 5 minutes before signing in to your Google account / Play Store. " +
                "If you sign in too early it can stay stuck on \"Checking info…\".\n\n" +
                "If that happens, close the Play Store, wait a minute and open it again.",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }, cancellable: true);

    static async Task<bool> SafeBootCompleted()
    {
        try { return await Adb.IsBootCompletedAsync(EmulatorService.Serial); } catch { return false; }
    }

    async Task MarkOnlineAsync()
    {
        _emuOnline = true;
        EmuStatusText.Text = "Phone: running ✅";
        await VirtualApps.RefreshAsync(true);
    }

    async void ShowPhone_Click(object sender, RoutedEventArgs e)
    {
        if (!_emuOnline)
        {
            MessageBox.Show(this, "The phone is not running. Click \"Start Phone\" first.", "MobileBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunBusy("Showing the phone window", async (_, _) => await EmulatorService.PreparePhoneAsync(bringToFront: true));
    }

    async void StopEmu_Click(object sender, RoutedEventArgs e) => await RunBusy("Stopping the virtual phone", async (p, _) =>
    {
        await _emu.StopAsync();
        _emuOnline = false;
        VirtualApps.Clear();
        EmuStatusText.Text = "Phone: stopped";
    });

    async void WipeEmu_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Factory reset erases all apps, accounts and data on the virtual phone and starts it fresh.\n\nContinue?",
                "Factory reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await StartPhoneAsync(wipe: true);
    }

    // ------------------------------------------------------------------ real phone

    async void MirrorSetup_Click(object sender, RoutedEventArgs e) => await RunBusy("Downloading mirroring tools", async (p, ct) =>
    {
        if (!Adb.Installed)
        {
            p.Report(new("Getting the package list from Google…"));
            _repo ??= await AndroidRepository.LoadAsync(ct);
            if (!EnsureLicenses(new[] { _repo.PlatformTools })) return;
            await SdkInstaller.InstallZipPackageAsync(_repo.PlatformTools, AppPaths.PlatformTools, "platform tools", p, ct);
            await Adb.StartServerAsync();
        }
        await ScrcpyService.InstallAsync(p, ct);
        RefreshSetupStatus();
        await TickAsync();
        MessageBox.Show(this, "Tools are ready. Now connect your phone (Step 2).", "MobileBridge",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }, cancellable: true);

    async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        if (!Adb.Installed)
        {
            MessageBox.Show(this, "Click \"Download tools\" first (Step 1).", "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await TickAsync();
        await RealApps.RefreshAsync(true);
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var address = PairAddressBox.Text.Trim();
        var code = PairCodeBox.Text.Trim();
        if (!Adb.Installed || address.Length == 0 || code.Length == 0)
        {
            MessageBox.Show(this, Adb.Installed ? "Enter the pairing IP:port and the 6-digit code shown on the phone." : "Click \"Download tools\" first.",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunBusy("Pairing with the phone", async (_, ct) =>
        {
            var r = await Adb.RunAsync(TimeSpan.FromSeconds(40), ct, "pair", address, code);
            Log.Info("adb pair: " + r.All);
            if (!r.All.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pairing failed:\n" + r.All);
            MessageBox.Show(this,
                "Paired!\n\nNow type the IP address & port shown at the top of the phone's \"Wireless debugging\" screen (not the pairing port) into \"Phone IP:port\" and click Connect.",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var address = ConnectAddressBox.Text.Trim();
        if (!Adb.Installed || address.Length == 0)
        {
            MessageBox.Show(this, Adb.Installed ? "Enter the phone's IP:port from the Wireless debugging screen." : "Click \"Download tools\" first.",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunBusy("Connecting to the phone", async (_, ct) =>
        {
            var r = await Adb.RunAsync(TimeSpan.FromSeconds(40), ct, "connect", address);
            Log.Info("adb connect: " + r.All);
            if (!r.All.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                r.All.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                r.All.Contains("failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Could not connect:\n" + r.All);
            await TickAsync();
        });
    }

    void StartMirror_Click(object sender, RoutedEventArgs e)
    {
        if (ScrcpyService.ExePath == null || !Adb.Installed)
        {
            MessageBox.Show(this, "Click \"Download tools\" first (Step 1).", "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DevicesList.SelectedItem is not AdbDevice { State: "device" } device)
        {
            MessageBox.Show(this,
                "Connect your phone and select it in the \"Detected phones\" list.\n\nIf it says \"unauthorized\", unlock the phone and tap \"Allow\" on the USB debugging prompt.",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int maxSize = int.TryParse((MaxSizeBox.SelectedItem as ComboBoxItem)?.Tag as string, out var m) ? m : 0;
        var opts = new MirrorOptions(maxSize, ScreenOffBox.IsChecked == true, AudioBox.IsChecked == true, StayAwakeBox.IsChecked == true);
        try
        {
            var proc = ScrcpyService.Start(device, opts);
            StatusText.Text = $"Mirroring {device.Model}. Close the mirror window to stop.";
            proc.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                int code = proc.ExitCode;
                Log.Info($"Mirroring ended (exit code {code}).");
                StatusText.Text = "Mirroring stopped.";
                if (code != 0)
                    MessageBox.Show(this,
                        code == 2 ? "The phone was disconnected." : "Mirroring stopped because of an error. See the Log tab for details.",
                        "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
                proc.Dispose();
            });
        }
        catch (Exception ex)
        {
            Log.Info("Could not start mirroring: " + ex);
            MessageBox.Show(this, ex.Message, "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ settings

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where MobileBridge stores its files" };
        if (dlg.ShowDialog(this) != true) return;
        var chosen = Path.Combine(dlg.FolderName, "MobileBridgeData");
        if (MessageBox.Show(this,
                $"New storage folder:\n{chosen}\n\nFiles already downloaded are NOT moved; you will need to run \"Download & Set Up\" again. MobileBridge will restart.\n\nContinue?",
                "Change storage folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        AppSettings.Current.DataRoot = chosen;
        AppSettings.Current.Save();
        Restart();
    }

    static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
    }

    async void DeletePhone_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete the virtual phone with all its apps and data?\n(The downloaded emulator and Android image are kept.)",
                "Delete virtual phone", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusy("Deleting the virtual phone", async (_, _) =>
        {
            if (await _emu.IsRunningAsync()) await _emu.StopAsync();
            EmulatorService.DeleteAvd();
            _emuOnline = false;
            VirtualApps.Clear();
            Log.Info("Virtual phone deleted.");
        });
    }

    async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove everything MobileBridge downloaded and the virtual phone?\n\n{AppPaths.Root}",
                "Remove everything", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusy("Removing downloaded files", async (_, _) =>
        {
            if (await _emu.IsRunningAsync()) await _emu.StopAsync();
            if (Adb.Installed)
                try { await Adb.RunAsync(TimeSpan.FromSeconds(15), default, "kill-server"); } catch { }
            await Task.Delay(1000);
            foreach (var dir in new[] { AppPaths.Sdk, AppPaths.AvdHome, AppPaths.ScrcpyDir, AppPaths.Downloads, AppPaths.Temp })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex) { Log.Info($"Could not delete {dir}: {ex.Message}"); }
            }
            _repo = null;
            _accelOk = null;
            _emuOnline = false;
            VirtualApps.Clear();
            DevicesList.ItemsSource = null;
            Log.Info("Removed downloaded components.");
        });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e) => OpenFile(AppPaths.LogFile);
    void OpenEmuLog_Click(object sender, RoutedEventArgs e) => OpenFile(AppPaths.EmulatorLog);

    void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "That log file does not exist yet.", "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
