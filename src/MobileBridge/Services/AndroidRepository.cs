using System.Xml.Linq;

namespace MobileBridge.Services;

public sealed record RemotePackage(string Path, string DisplayName, Version Revision, string Url, long Size,
    string? Sha1, string? LicenseId);

/// <summary>Reads Google's official Android SDK repository index (the same one sdkmanager uses).</summary>
public sealed class AndroidRepository
{
    public const string RepoBase = "https://dl.google.com/android/repository/";
    public const string PlayImagesBase = RepoBase + "sys-img/google_apis_playstore/";

    public Dictionary<string, string> Licenses { get; } = new();
    public List<RemotePackage> Packages { get; } = new();

    public static async Task<AndroidRepository> LoadAsync(CancellationToken ct)
    {
        var r = new AndroidRepository();
        var main = Downloader.GetStringAsync(RepoBase + "repository2-3.xml", ct);
        var images = Downloader.GetStringAsync(PlayImagesBase + "sys-img2-3.xml", ct);
        r.Parse(XDocument.Parse(await main), RepoBase);
        r.Parse(XDocument.Parse(await images), PlayImagesBase);
        Log.Info($"Loaded Google repository: {r.Packages.Count} packages for Windows.");
        return r;
    }

    void Parse(XDocument doc, string baseUrl)
    {
        var root = doc.Root!;
        foreach (var lic in root.Elements().Where(e => e.Name.LocalName == "license"))
        {
            var id = (string?)lic.Attribute("id");
            if (id != null) Licenses[id] = lic.Value.Trim();
        }

        foreach (var rp in root.Elements().Where(e => e.Name.LocalName == "remotePackage"))
        {
            var path = (string?)rp.Attribute("path");
            if (path == null) continue;
            var channel = (string?)Child(rp, "channelRef")?.Attribute("ref") ?? "channel-0";
            if (channel != "channel-0") continue; // stable only

            var rev = Child(rp, "revision");
            var version = new Version(Int(rev, "major"), Int(rev, "minor"), Int(rev, "micro"));
            var display = Child(rp, "display-name")?.Value.Trim() ?? path;
            var license = (string?)Child(rp, "uses-license")?.Attribute("ref");
            var archives = Child(rp, "archives");
            if (archives == null) continue;

            foreach (var ar in archives.Elements().Where(e => e.Name.LocalName == "archive"))
            {
                var os = Child(ar, "host-os")?.Value.Trim();
                if (os != null && os != "windows") continue;
                var arch = Child(ar, "host-arch")?.Value.Trim();
                if (arch != null && arch != "x64") continue;
                var complete = Child(ar, "complete");
                var url = Child(complete, "url")?.Value.Trim();
                if (string.IsNullOrEmpty(url)) continue;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = baseUrl + url;
                long.TryParse(Child(complete, "size")?.Value, out var size);
                var sha = Child(complete, "checksum")?.Value.Trim();
                Packages.Add(new RemotePackage(path, display, version, url, size, sha, license));
                break;
            }
        }
    }

    static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    static int Int(XElement? e, string name) => int.TryParse(Child(e, name)?.Value, out var v) ? v : 0;

    public RemotePackage? Find(string path) =>
        Packages.Where(p => p.Path == path).OrderByDescending(p => p.Revision).FirstOrDefault();

    public RemotePackage PlatformTools =>
        Find("platform-tools") ?? throw new InvalidOperationException("Platform tools were not found in Google's repository.");

    public RemotePackage Emulator =>
        Find("emulator") ?? throw new InvalidOperationException("The emulator was not found in Google's repository.");

    public RemotePackage PlayStoreImage(int api) =>
        Find($"system-images;android-{api};google_apis_playstore;x86_64")
        ?? throw new InvalidOperationException($"Google does not offer an Android API {api} Google Play image for Windows right now. Pick another Android version.");
}

public static class SdkInstaller
{
    const string Marker = ".mobilebridge-package";

    public static bool SystemImageInstalled(int api)
    {
        var dir = AppPaths.SystemImageDir(api);
        return File.Exists(Path.Combine(dir, "system.img")) && File.Exists(Path.Combine(dir, Marker));
    }

    /// <summary>Downloads a repository zip and installs its content into <paramref name="targetDir"/>.</summary>
    public static async Task InstallZipPackageAsync(RemotePackage pkg, string targetDir, string label,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var zip = Path.Combine(AppPaths.Downloads, Path.GetFileName(new Uri(pkg.Url).AbsolutePath));
        await Downloader.DownloadFileAsync(pkg.Url, zip, pkg.Size, pkg.Sha1, label, progress, ct);

        var tmp = Path.Combine(AppPaths.Temp, Guid.NewGuid().ToString("N"));
        try
        {
            await Archive.ExtractAsync(zip, tmp, label, progress, ct);
            progress.Report(new($"Installing {label}…"));
            var content = tmp;
            var dirs = Directory.GetDirectories(tmp);
            if (dirs.Length == 1 && Directory.GetFiles(tmp).Length == 0) content = dirs[0];

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(content, targetDir);
            File.WriteAllText(Path.Combine(targetDir, Marker), $"{pkg.Path}\n{pkg.Revision}\n");
            Log.Info($"Installed {label} ({pkg.Path} r{pkg.Revision}).");
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
        try { File.Delete(zip); } catch { }
    }
}
