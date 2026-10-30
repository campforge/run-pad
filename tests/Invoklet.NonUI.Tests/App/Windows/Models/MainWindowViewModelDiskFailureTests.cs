using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

// The file tree operations against a fake disk: what the view model does when the disk refuses, without a disk.
public class MainWindowViewModelDiskFailureTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "never-created-root");

    private static (MainWindowViewModel Vm, Mock<IWindowService> Windows, Mock<IDiskOperations> Disk) CreateViewModel()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(Root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var disk = new Mock<IDiskOperations>();
        var vm = new MainWindowViewModel([], fileSystem.Object, windows.Object, prefs.Object, new LanguageHostRegistry(), disk: disk.Object);
        return (vm, windows, disk);
    }

    [Fact]
    public async Task DeleteFile_DiskRefuses_ShowsTheErrorWithTheReason()
    {
        // Arrange
        var (vm, windows, disk) = CreateViewModel();
        var path = Path.Combine(Root, "a.inv");
        disk.Setup(d => d.Delete(path, false)).Throws(new IOException("in use"));

        // Act
        await vm.DeleteFile(path);

        // Assert
        windows.Verify(w => w.ShowErrorAsync("Delete Failed", It.Is<string>(m => m.Contains("a.inv") && m.Contains("in use"))), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_AFolder_AsksTheDiskToDeleteItAsAFolder()
    {
        // Arrange
        var (vm, _, disk) = CreateViewModel();
        var path = Path.Combine(Root, "sub");
        disk.Setup(d => d.IsDirectory(path)).Returns(true);

        // Act
        await vm.DeleteFile(path);

        // Assert
        disk.Verify(d => d.Delete(path, true), Times.Once);
    }

    [Fact]
    public async Task RenameFile_DiskRefuses_ShowsTheErrorNamingTheTarget()
    {
        // Arrange
        var (vm, windows, disk) = CreateViewModel();
        var path = Path.Combine(Root, "a.inv");
        disk.Setup(d => d.Move(path, Path.Combine(Root, "b.inv"), false)).Throws(new IOException("exists"));

        // Act
        await vm.RenameFile(path, "b");

        // Assert
        windows.Verify(w => w.ShowErrorAsync("Rename Failed", It.Is<string>(m => m.Contains("to 'b.inv'") && m.Contains("exists"))), Times.Once);
    }

    [Fact]
    public async Task MoveFileOrFolder_DiskRefuses_ShowsTheErrorNamingTheFolder()
    {
        // Arrange
        var (vm, windows, disk) = CreateViewModel();
        var source = Path.Combine(Root, "a.inv");
        var targetFolder = Path.Combine(Root, "Sub");
        disk.Setup(d => d.IsDirectory(targetFolder)).Returns(true);
        disk.Setup(d => d.Move(source, Path.Combine(targetFolder, "a.inv"), false)).Throws(new IOException("denied"));

        // Act
        await vm.MoveFileOrFolder(source, targetFolder);

        // Assert
        windows.Verify(w => w.ShowErrorAsync("Move Failed", It.Is<string>(m => m.Contains("into 'Sub'") && m.Contains("denied"))), Times.Once);
    }

    [Fact]
    public async Task MoveFileOrFolder_DroppedWhereItAlreadyIs_NeverTouchesTheDisk()
    {
        // Arrange
        var (vm, _, disk) = CreateViewModel();
        var source = Path.Combine(Root, "a.inv");

        // Act
        await vm.MoveFileOrFolder(source, null);

        // Assert
        disk.Verify(d => d.Move(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
