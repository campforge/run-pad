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
    public async Task InstallNetcoredbgAsync_Should_ExtractTheRealExecutable_ToTheAppDataDirectory()
    {
        var downloader = new Mock<ISetupDownloader>();
        downloader
            .Setup(d => d.DownloadFileAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Uri _, string destinationPath, CancellationToken _) =>
            {
                WriteFakeNetcoredbgArchive(destinationPath);
                return Task.CompletedTask;
            });
        var processRunner = new Mock<ISetupProcessRunner>();

        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "netcoredbg");
        if (Directory.Exists(installDir))
            Directory.Delete(installDir, recursive: true);
        try
        {
            var service = new SetupInstallerService(downloader.Object, processRunner.Object);

            var exePath = await service.InstallNetcoredbgAsync(
                new Progress<string>(), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(exePath));
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
            .Setup(d => d.DownloadFileAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Uri _, string destinationPath, CancellationToken _) =>
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
            () => service.InstallDotnetSdkAsync(new Progress<string>(), TestContext.Current.CancellationToken));
    }

    // Builds a real gzip-compressed tar archive (matching what netcoredbg-linux-*.tar.gz actually is)
    // containing a single "netcoredbg" file, using only .NET's own tar/gzip APIs — no external `tar`
    // binary, so this works the same on every OS this test suite runs on.
    private static void WriteFakeNetcoredbgArchive(string destinationPath)
    {
        using var fileStream = File.Create(destinationPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tarWriter = new TarWriter(gzipStream);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg");
        var content = "fake netcoredbg binary"u8.ToArray();
        entry.DataStream = new MemoryStream(content);
        tarWriter.WriteEntry(entry);
    }
}
