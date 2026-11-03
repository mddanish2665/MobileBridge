using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MobileBridge.Services;

namespace MobileBridge;

public interface IBusyHost
{
    Task RunBusy(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work, bool cancellable = false);
}

/// <summary>Install / list / open / uninstall apps on one device (virtual or real).</summary>
public partial class AppsPanel : UserControl
{
    public IBusyHost? Host { get; set; }
    public Func<string?> SerialProvider { get; set; } = () => null;
    public string NoDeviceMessage { get; set; } = "No device is connected.";

    List<string> _all = new();

    public AppsPanel() => InitializeComponent();

    Window? Owner => Window.GetWindow(this);

    string? RequireSerial()
    {
        var s = SerialProvider();
        if (s == null) MessageBox.Show(Owner!, NoDeviceMessage, "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        return s;
    }

    public void Clear()
    {
        _all = new();
        ApplyFilter();
        CountText.Text = "No device";
    }

    public async Task RefreshAsync(bool quiet)
    {
        var serial = SerialProvider();
        if (serial == null)
        {
            Clear();
            if (!quiet) RequireSerial();
            return;
        }
        try
        {
            CountText.Text = "Loading…";
            _all = await Adb.GetUserPackagesAsync(serial);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Info("Could not list apps: " + ex.Message);
            CountText.Text = "Could not load the app list (see Log).";
        }
    }

    void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        AppsList.ItemsSource = f.Length == 0 ? _all : _all.Where(p => p.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        CountText.Text = $"{_all.Count} apps you installed";
    }

    async Task InstallFilesAsync(string serial, string[] files)
    {
        if (Host == null) return;
        await Host.RunBusy("Installing apps", async (p, ct) =>
        {
            try
            {
                await ApkInstaller.InstallAsync(serial, files, p, ct);
            }
            finally
            {
                await RefreshAsync(true);
            }
            MessageBox.Show(Owner!,
                (files.Length == 1 ? "App installed successfully." : $"{files.Length} apps installed successfully.") +
                "\n\nYou can find it in the phone's app drawer (swipe up on the home screen), or select it here and click \"Open app\".",
                "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }, cancellable: true);
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var serial = RequireSerial();
        if (serial == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose Android apps to install",
            Filter = "Android apps (*.apk;*.xapk;*.apks)|*.apk;*.xapk;*.apks|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Owner) == true) await InstallFilesAsync(serial, dlg.FileNames);
    }

    void Drop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Drop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        var files = dropped.Where(f => ApkInstaller.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
        if (files.Length == 0)
        {
            MessageBox.Show(Owner!, "Please drop .apk, .xapk or .apks files.", "MobileBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var serial = RequireSerial();
        if (serial != null) await InstallFilesAsync(serial, files);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(false);

    void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    async void AppsList_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    async Task OpenSelectedAsync()
    {
        if (AppsList.SelectedItem is not string pkg || Host == null) return;
        var serial = RequireSerial();
        if (serial == null) return;
        await Host.RunBusy($"Opening {pkg}", async (_, _) => await Adb.LaunchAsync(serial, pkg));
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not string pkg || Host == null) return;
        var serial = RequireSerial();
        if (serial == null) return;
        if (MessageBox.Show(Owner!, $"Uninstall {pkg} and delete its data?", "Uninstall app",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Host.RunBusy($"Uninstalling {pkg}", async (_, _) =>
        {
            await Adb.UninstallAsync(serial, pkg);
            Log.Info($"Uninstalled {pkg}");
            await RefreshAsync(true);
        });
    }
}
