using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DotPad.Services.Setup;

// First-run setup wizard's installer — fetches the .NET SDK and netcoredbg from their real, official
// sources (TODO.md, live report 2026-07-25: verified against the actual dotnet-install.sh/.ps1 scripts
// and the real Samsung/netcoredbg GitHub releases API, not guessed) instead of leaving the user to
// find and install both themselves before DotPad's Run/Debug buttons do anything useful.
public sealed class SetupInstallerService(ISetupDownloader downloader, ISetupProcessRunner processRunner) : ISetupInstallerService
{
    private const string NetcoredbgLatestBase = "https://github.com/Samsung/netcoredbg/releases/latest/download";

    // netcoredbg's newest release (3.2.0) dropped the osx-amd64 (Intel Mac) build that every release
    // back to 3.1.0 shipped — pin the last release known to have one for that specific platform/arch
    // combo only; every other platform/arch tracks the real "latest" release.
    private const string NetcoredbgIntelMacPinnedTag = "3.1.3-1062";

    public async Task<string> InstallDotnetSdkAsync(IProgress<string> progress, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var scriptUrl = isWindows
            ? new Uri("https://dot.net/v1/dotnet-install.ps1")
            : new Uri("https://dot.net/v1/dotnet-install.sh");

        var scriptPath = Path.Combine(Path.GetTempPath(), isWindows ? "dotpad-dotnet-install.ps1" : "dotpad-dotnet-install.sh");
        progress.Report("Downloading the official .NET install script...");
        await downloader.DownloadFileAsync(scriptUrl, scriptPath, ct);

        string exe;
        List<string> args;
        if (isWindows)
        {
            exe = "powershell";
            args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Channel", "10.0"];
        }
        else
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            exe = "bash";
            args = [scriptPath, "--channel", "10.0"];
        }

        progress.Report("Running the .NET SDK installer (this can take a few minutes)...");
        var exitCode = await processRunner.RunAsync(exe, args, progress, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"dotnet-install exited with code {exitCode}.");

        var installDir = ResolveDotnetInstallDir(CurrentPlatform());
        var dotnetExe = Path.Combine(installDir, isWindows ? "dotnet.exe" : "dotnet");

        if (!File.Exists(dotnetExe))
            throw new InvalidOperationException($"dotnet-install finished but {dotnetExe} wasn't found.");

        // dotnet-install runs as a separate child process — any PATH update it makes doesn't
        // propagate back to us. Prepend the install dir to OUR OWN process's PATH so
        // DotnetSdkProvider (`dotnet --list-sdks`) finds it immediately, without an app restart.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", installDir + Path.PathSeparator + pathVar);

        return dotnetExe;
    }

    public async Task<string> InstallNetcoredbgAsync(IProgress<string> progress, CancellationToken ct)
    {
        var (assetName, downloadUrl) = ResolveNetcoredbgAsset();

        var archivePath = Path.Combine(Path.GetTempPath(), assetName);
        progress.Report($"Downloading {assetName}...");
        await downloader.DownloadFileAsync(downloadUrl, archivePath, ct);

        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotPad", "netcoredbg");
        if (Directory.Exists(installDir))
            Directory.Delete(installDir, recursive: true);
        Directory.CreateDirectory(installDir);

        progress.Report("Extracting...");
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(archivePath, installDir);
        else
        {
            // TarFile.ExtractToDirectoryAsync(string, ...) does NOT auto-decompress gzip — the
            // netcoredbg-{linux,osx}-*.tar.gz assets are gzip-compressed, so this needs the
            // Stream-accepting overload wrapped in a GZipStream, not the plain path overload
            // (verified the hard way — a first attempt using the path overload directly threw
            // EndOfStreamException, caught by SetupInstallerServiceTests before this ever shipped).
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzipStream, installDir, overwriteFiles: true, ct);
        }

        var exeName = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var exePath = Path.Combine(installDir, exeName);
        if (!File.Exists(exePath))
            throw new InvalidOperationException($"netcoredbg archive extracted but {exePath} wasn't found.");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return exePath;
    }

    // dotnet-install's own default install location when -InstallDir isn't passed (verified against
    // the real scripts, not documentation) — Windows: %LocalAppData%\Microsoft\dotnet; Linux/macOS:
    // $HOME/.dotnet. Both are per-user; neither needs admin/sudo. Platform taken as a parameter for
    // the same reason as ResolveNetcoredbgAsset — testable regardless of which OS runs the test.
    internal static string ResolveDotnetInstallDir(SetupOSPlatform platform) => platform switch
    {
        SetupOSPlatform.Windows => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet"),
        _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
    };

    private static (string AssetName, Uri Url) ResolveNetcoredbgAsset() =>
        ResolveNetcoredbgAsset(CurrentPlatform(), RuntimeInformation.OSArchitecture);

    private static SetupOSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? SetupOSPlatform.Windows :
        OperatingSystem.IsMacOS() ? SetupOSPlatform.MacOS : SetupOSPlatform.Linux;

    // OS/arch taken as parameters (rather than read directly via OperatingSystem.Is*()/
    // RuntimeInformation.OSArchitecture inside this method) purely so this mapping can be unit
    // tested for every platform/arch branch regardless of which OS the test suite itself runs on.
    internal static (string AssetName, Uri Url) ResolveNetcoredbgAsset(SetupOSPlatform platform, Architecture arch)
    {
        switch (platform)
        {
            case SetupOSPlatform.Windows:
                return Asset("netcoredbg-win64.zip", latest: true);
            case SetupOSPlatform.MacOS:
                return arch == Architecture.Arm64
                    ? Asset("netcoredbg-osx-arm64.zip", latest: true)
                    : Asset("netcoredbg-osx-amd64.tar.gz", latest: false);
            default: // Linux — the only other platform DotPad ships for.
                return arch == Architecture.Arm64
                    ? Asset("netcoredbg-linux-arm64.tar.gz", latest: true)
                    : Asset("netcoredbg-linux-amd64.tar.gz", latest: true);
        }

        static (string, Uri) Asset(string name, bool latest)
        {
            var url = latest
                ? $"{NetcoredbgLatestBase}/{name}"
                : $"https://github.com/Samsung/netcoredbg/releases/download/{NetcoredbgIntelMacPinnedTag}/{name}";
            return (name, new Uri(url));
        }
    }
}

public enum SetupOSPlatform { Windows, MacOS, Linux }
