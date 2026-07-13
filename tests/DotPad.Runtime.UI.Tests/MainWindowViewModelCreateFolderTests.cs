using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #69: file tree had no "create new folder" command at all. CreateFolder resolves WHERE the
// new folder actually goes (the interesting/risky part — see the three cases below) and delegates
// the actual directory creation + unique-name probing to IFileSystemService.CreateNewFolder.
public class MainWindowViewModelCreateFolderTests
{
    private static MainWindowViewModel CreateViewModel(Mock<IFileSystemService> fileSystem)
    {
        fileSystem.Setup(f => f.Folder).Returns("/root");
        fileSystem.Setup(f => f.GetFilesTree()).Returns([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        return new MainWindowViewModel(
            sdkProviders: [],
            fileSystemSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object);
    }

    [Fact]
    public void CreateFolder_Should_UseTheRoot_WhenNothingIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        var result = vm.CreateFolder(null);

        Assert.Equal("/root/New Folder", result);
        fileSystem.Verify(f => f.CreateNewFolder("/root"), Times.Once);
    }

    [Fact]
    public void CreateFolder_Should_CreateInsideTheSelectedFolder_WhenAFolderIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        Directory.CreateDirectory("/tmp/dotpad-createfolder-test/Sub");
        try
        {
            fileSystem.Setup(f => f.CreateNewFolder("/tmp/dotpad-createfolder-test/Sub"))
                .Returns("/tmp/dotpad-createfolder-test/Sub/New Folder");
            var vm = CreateViewModel(fileSystem);

            var result = vm.CreateFolder("/tmp/dotpad-createfolder-test/Sub");

            Assert.Equal("/tmp/dotpad-createfolder-test/Sub/New Folder", result);
            fileSystem.Verify(f => f.CreateNewFolder("/tmp/dotpad-createfolder-test/Sub"), Times.Once);
        }
        finally { Directory.Delete("/tmp/dotpad-createfolder-test", recursive: true); }
    }

    [Fact]
    public void CreateFolder_Should_CreateAlongsideTheSelectedFile_WhenAFileIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        // "/root/query.rpad" doesn't need to exist on disk — Directory.Exists on it is false (it's
        // not a directory), so the file branch (containing folder) is taken regardless.
        var result = vm.CreateFolder("/root/query.rpad");

        Assert.Equal("/root/New Folder", result);
        fileSystem.Verify(f => f.CreateNewFolder("/root"), Times.Once);
    }

    [Fact]
    public void CreateFolder_Should_RefreshTheTree_AfterCreating()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");
        var vm = CreateViewModel(fileSystem);

        vm.CreateFolder(null);

        fileSystem.Verify(f => f.GetFilesTree(), Times.AtLeastOnce);
    }
}
