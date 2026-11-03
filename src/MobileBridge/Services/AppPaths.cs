using System.Text.Json;

namespace MobileBridge.Services;

/// <summary>User settings, stored in %LOCALAPPDATA%\MobileBridge\settings.json.</summary>
public sealed class AppSettings
{
    public string? DataRoot { get; set; }
    public int AndroidApi { get; set; } = 34;
    public List<string> AcceptedLicenses { get; set; } = new();

    static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MobileBridge");
    static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static string DefaultDataRoot => SettingsDir;
    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new();
        }
        catch { /* corrupt settings -> defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>All folders MobileBridge downloads into. Everything lives under one data folder.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var r = AppSettings.Current.DataRoot;
            return string.IsNullOrWhiteSpace(r) ? AppSettings.DefaultDataRoot : r;
        }
    }

    public static string Sdk => Path.Combine(Root, "sdk");
    public static string PlatformTools => Path.Combine(Sdk, "platform-tools");
    public static string Adb => Path.Combine(PlatformTools, "adb.exe");
    public static string EmulatorDir => Path.Combine(Sdk, "emulator");
    public static string EmulatorExe => Path.Combine(EmulatorDir, "emulator.exe");
    public static string SystemImageDir(int api) =>
        Path.Combine(Sdk, "system-images", $"android-{api}", "google_apis_playstore", "x86_64");
    public static string AvdHome => Path.Combine(Root, "avd");
    public static string ScrcpyDir => Path.Combine(Root, "scrcpy");
    public static string Downloads => Path.Combine(Root, "downloads");
    public static string Temp => Path.Combine(Root, "temp");
    public static string Logs => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(Logs, "mobilebridge.log");
    public static string EmulatorLog => Path.Combine(Logs, "emulator.log");
}

public static class Log
{
    static readonly object Gate = new();
    public static event Action<string>? Line;

    public static void Info(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch { /* logging must never crash the app */ }
        Line?.Invoke(line);
    }
}

public sealed record ProgressInfo(string Text, double Percent = -1);
