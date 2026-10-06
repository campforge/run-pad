using System.Formats.Tar;
using System.IO.Compression;
using Moq;
using RunPad.Services.Setup;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Only the network (ISetupDownloader) and process-spawn (ISetupProcessRunner) boundaries are faked —
// archive extraction, path resolution and file-system placement all run for real, same "fake only the
// external boundary, not the logic under test" pattern used by CSharpDecompilerServiceTests etc.
public class SetupInstallerServiceTests
{
    [Fact]
    public async Task InstallNetcoredbgAsync_Should_ExtractTheRealExecutable_ToTheInstallDirectory()
    {
        var downloader = new Mock<ISetupDownloader>();
        downloader
            .Setup(d => d.DownloadFileAsync(
                It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((Uri _, string destinationPath, IProgress<double>? _, CancellationToken _) =>
            {
                WriteFakeNetcoredbgArchive(destinationPath);
                return Task.CompletedTask;
            });
        var processRunner = new Mock<ISetupProcessRunner>();

        // A unique temp directory, never the user's real AppData installation — InstallNetcoredbgAsync
        // deletes its install directory before extracting.
        var installDir = Path.Combine(Path.GetTempPath(), "runpad-tests-netcoredbg-" + Guid.NewGuid());
        try
        {
            var service = new SetupInstallerService(downloader.Object, processRunner.Object, installDir);

            var exePath = await service.InstallNetcoredbgAsync(
                new Progress<SetupProgress>(), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(exePath));
            Assert.StartsWith(installDir, exePath);
            Assert.Equal(
                "fake netcoredbg binary", await File.ReadAllTextAsync(exePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallDotnetSdkAsync_Should_Throw_WhenTheInstallerScriptExitsNonZero()
    {
        var downloader = new Mock<ISetupDownloader>();
        downloader
            .Setup(d => d.DownloadFileAsync(
                It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((Uri _, string destinationPath, IProgress<double>? _, CancellationToken _) =>
            {
                File.WriteAllText(destinationPath, "# not a real dotnet-install script");
                return Task.CompletedTask;
            });
        var processRunner = new Mock<ISetupProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = new SetupInstallerService(downloader.Object, processRunner.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallDotnetSdkAsync(new Progress<SetupProgress>(), TestContext.Current.CancellationToken));
    }

    // InstallNetcoredbgAsync picks zip on Windows and macOS arm64, tar.gz elsewhere
    // (ResolveNetcoredbgAsset), so the fake archive has to match whichever one the OS actually
    // running this test resolves to — otherwise ZipFile.ExtractToDirectory chokes on tar.gz bytes
    // (or vice versa). Real releases for every platform (verified against the actual win64/
    // linux-amd64/linux-arm64/osx-arm64/osx-amd64 assets, not guessed) also nest the executable
    // under a top-level "netcoredbg/" folder rather than extracting flat — matched here too, so this
    // fixture exercises the same layout production sees. The zip entry uses "netcoredbg.exe" only on
    // Windows; macOS arm64's zip asset contains a plain "netcoredbg" binary (no .exe extension),
    // mirroring the non-Windows tar.gz assets.
    private static void WriteFakeNetcoredbgArchive(string destinationPath)
    {
        if (destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            WriteFakeZipArchive(destinationPath);
        else
            WriteFakeTarGzArchive(destinationPath);
    }

    private static void WriteFakeZipArchive(string destinationPath)
    {
        // ZipArchiveMode.Create (unlike the tar.gz helper's File.Create) throws if the file already
        // exists — real installs always start from a fresh %TEMP% path, but a stale leftover from a
        // previous test/manual run would otherwise fail this fixture, not the code under test.
        File.Delete(destinationPath);
        using var zip = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        // Windows uses "netcoredbg.exe"; macOS arm64's zip asset (like the tar.gz assets on other
        // non-Windows platforms) uses a plain "netcoredbg" binary without the .exe extension.
        var exeName = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var entry = zip.CreateEntry($"netcoredbg/{exeName}");
        using var stream = entry.Open();
        stream.Write("fake netcoredbg binary"u8);
    }

    // Builds a real gzip-compressed tar archive using only .NET's own tar/gzip APIs — no external
    // `tar` binary, so this works the same on every OS this test suite runs on.
    private static void WriteFakeTarGzArchive(string destinationPath)
    {
        using var fileStream = File.Create(destinationPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tarWriter = new TarWriter(gzipStream);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg/netcoredbg");
        var content = "fake netcoredbg binary"u8.ToArray();
        entry.DataStream = new MemoryStream(content);
        tarWriter.WriteEntry(entry);
    }
}
