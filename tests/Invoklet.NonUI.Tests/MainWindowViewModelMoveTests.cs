using Moq;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// File tree had no drag-and-drop at all. MoveFileOrFolder resolves the target directory
// the same way CreateFolderAsync/CreateFileAsync already do (ResolveParentDir — dropping onto a folder
// moves INSIDE it, onto a file moves alongside it, onto empty tree space moves to the root), then
// shares its actual File.Move/Directory.Move + open-tab bookkeeping with RenameFile via the new
// MoveOrRenameOnDisk (see MainWindowViewModelDeleteRenameFolderTests.cs for the equivalent Rename
// coverage — not re-tested here to avoid duplicating that file's own directory-vs-file split).
public class MainWindowViewModelMoveTests
{
    private static (MainWindowViewModel Vm, Mock<IWindowService> WindowService) CreateViewModel(string folder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var windowService = new Mock<IWindowService>();
        windowService.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, windowService);
    }

    [Fact]
    public void MoveFileOrFolder_Should_MoveAPlainFile_IntoTheDroppedOnFolder()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");

            var (vm, _) = CreateViewModel(root);
            vm.MoveFileOrFolder(file, sub);

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(sub, "a.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MoveFileOrFolder_Should_MoveADirectory_Recursively()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var target = Path.Combine(root, "Target");
            Directory.CreateDirectory(target);
            var source = Path.Combine(root, "Source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.rpad"), "content");

            var (vm, _) = CreateViewModel(root);
            vm.MoveFileOrFolder(source, target);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Combine(target, "Source")));
            Assert.True(File.Exists(Path.Combine(target, "Source", "a.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MoveFileOrFolder_Should_MoveToTheRoot_WhenTargetPathIsNull()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(sub, "a.rpad");
            File.WriteAllText(file, "content");

            var (vm, _) = CreateViewModel(root);
            vm.MoveFileOrFolder(file, null);

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(root, "a.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Dropping onto a FILE node (not a folder) moves alongside it — same "containing folder" semantics
    // ResolveParentDir already gives CreateFolderAsync/CreateFileAsync for a selected file.
    [Fact]
    public void MoveFileOrFolder_Should_MoveAlongsideAFile_WhenTheDropTargetIsAFileNotAFolder()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var targetFile = Path.Combine(sub, "b.rpad");
            File.WriteAllText(targetFile, "content");
            var sourceFile = Path.Combine(root, "a.rpad");
            File.WriteAllText(sourceFile, "content");

            var (vm, _) = CreateViewModel(root);
            vm.MoveFileOrFolder(sourceFile, targetFile);

            Assert.False(File.Exists(sourceFile));
            Assert.True(File.Exists(Path.Combine(sub, "a.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Note: File.Move(x, x) is a silent no-op on its own (a same-path rename() at the OS level, at
    // least on Linux) — it wouldn't throw and leave the file misplaced even WITHOUT this guard, so
    // "the file still exists at the same path" alone doesn't actually prove the guard is doing
    // anything. What the guard actually earns is skipping the CloseTab/OpenTab churn around it —
    // pointless for a real no-op, and potentially disruptive if the tab has unsaved changes — so this
    // asserts the exact same tab VM instance survives untouched (a fresh CloseTab+OpenTab cycle would
    // produce a new instance).
    [Fact]
    public void MoveFileOrFolder_Should_DoNothing_WhenDroppedOntoItsOwnCurrentParent()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");

            var (vm, windowService) = CreateViewModel(root);
            vm.OpenTab(file);
            var tabBefore = vm.Tabs.Single(t => t.FilePath == file);

            vm.MoveFileOrFolder(file, null); // already at the root

            Assert.True(File.Exists(file));
            Assert.Same(tabBefore, vm.Tabs.Single(t => t.FilePath == file));
            windowService.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MoveFileOrFolder_Should_UpdateTheOpenTab_ToTheNewPath()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");

            var (vm, _) = CreateViewModel(root);
            vm.OpenTab(file);
            Assert.Contains(vm.Tabs, t => t.FilePath == file);

            vm.MoveFileOrFolder(file, sub);

            var newPath = Path.Combine(sub, "a.rpad");
            Assert.Contains(vm.Tabs, t => t.FilePath == newPath);
            Assert.DoesNotContain(vm.Tabs, t => t.FilePath == file);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MoveFileOrFolder_Should_UpdateNestedOpenTabs_WhenMovingAFolder()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var target = Path.Combine(root, "Target");
            Directory.CreateDirectory(target);
            var source = Path.Combine(root, "Source");
            Directory.CreateDirectory(source);
            var nested = Path.Combine(source, "a.rpad");
            File.WriteAllText(nested, "content");

            var (vm, _) = CreateViewModel(root);
            vm.OpenTab(nested);
            Assert.Contains(vm.Tabs, t => t.FilePath == nested);

            vm.MoveFileOrFolder(source, target);

            var newNestedPath = Path.Combine(target, "Source", "a.rpad");
            Assert.Contains(vm.Tabs, t => t.FilePath == newNestedPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Directory.Move already throws a real, sensible IOException for this on its own (moving a
    // directory into one of its own subdirectories) — no separate pre-check needed, just the same
    // reopen-on-failure/error-dialog path RenameFile already has.
    [Fact]
    public void MoveFileOrFolder_Should_ReportAnError_AndKeepTheOriginalTabOpen_WhenMovingAFolderIntoItsOwnDescendant()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-move-test").FullName;
        try
        {
            var parent = Path.Combine(root, "Parent");
            var child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);
            var nested = Path.Combine(parent, "a.rpad");
            File.WriteAllText(nested, "content");

            var (vm, windowService) = CreateViewModel(root);
            vm.OpenTab(nested);

            vm.MoveFileOrFolder(parent, child);

            windowService.Verify(w => w.ShowErrorAsync(
                "Move Failed", It.Is<string>(m => m.Contains("Parent"))), Times.Once);
            Assert.True(Directory.Exists(parent));
            Assert.Contains(vm.Tabs, t => t.FilePath == nested);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
