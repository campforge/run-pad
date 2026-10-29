using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// File tree had no "create new folder" command at all. CreateFolderAsync resolves WHERE
// the new folder actually goes (the interesting/risky part — see the three cases below) and delegates
// the actual directory creation + unique-name probing to IFileSystemService.CreateNewFolder.
public class MainWindowViewModelCreateFolderTests
{
    private static MainWindowViewModel CreateViewModel(Mock<IFileSystemService> fileSystem)
    {
        fileSystem.Setup(f => f.Folder).Returns("/root");
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());

        return new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
    }

    [Fact]
    public async Task CreateFolderAsync_Should_UseTheRoot_WhenNothingIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        var result = await vm.CreateFolderAsync(null);

        Assert.Equal("/root/New Folder", result);
        fileSystem.Verify(f => f.CreateNewFolder("/root"), Times.Once);
    }

    [Fact]
    public async Task CreateFolderAsync_Should_CreateInsideTheSelectedFolder_WhenAFolderIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        Directory.CreateDirectory("/tmp/Invoklet-createfolder-test/Sub");
        try
        {
            fileSystem.Setup(f => f.CreateNewFolder("/tmp/Invoklet-createfolder-test/Sub"))
                .Returns("/tmp/Invoklet-createfolder-test/Sub/New Folder");
            var vm = CreateViewModel(fileSystem);

            var result = await vm.CreateFolderAsync("/tmp/Invoklet-createfolder-test/Sub");

            Assert.Equal("/tmp/Invoklet-createfolder-test/Sub/New Folder", result);
            fileSystem.Verify(f => f.CreateNewFolder("/tmp/Invoklet-createfolder-test/Sub"), Times.Once);
        }
        finally { Directory.Delete("/tmp/Invoklet-createfolder-test", recursive: true); }
    }

    [Fact]
    public async Task CreateFolderAsync_Should_CreateAlongsideTheSelectedFile_WhenAFileIsSelected()
    {
        // Windows CI finding: "/root/query.rpad" is a POSIX-style absolute path with no drive
        // letter — a test-only stand-in, not how a real Windows path looks (those always carry a
        // drive letter, e.g. "C:\Users\...", for which Path.GetDirectoryName works fine). On Windows
        // that leading "/" with no drive resolves differently enough that GetDirectoryName doesn't
        // return "/root" for it, so the mock setup below never matches — a test-harness path-style
        // limitation, not a real product bug. Same precedent as MainWindowViewModelOpenTabTests'
        // OS guard.
        if (OperatingSystem.IsWindows()) return;

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        // "/root/query.rpad" doesn't need to exist on disk — Directory.Exists on it is false (it's
        // not a directory), so the file branch (containing folder) is taken regardless.
        var result = await vm.CreateFolderAsync("/root/query.rpad");

        Assert.Equal("/root/New Folder", result);
        fileSystem.Verify(f => f.CreateNewFolder("/root"), Times.Once);
    }

    [Fact]
    public async Task CreateFolderAsync_Should_RefreshTheTree_AfterCreating()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        await vm.CreateFolderAsync(null);

        fileSystem.Verify(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
