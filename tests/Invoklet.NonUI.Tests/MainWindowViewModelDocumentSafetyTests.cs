using Moq;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Closing/renaming/moving/deleting used to remove the tab first and only then try to save it
// (EditorTabsControl's CollectionChanged handler), so a failed write lost the edits along with the
// tab. Unsaved content is now resolved BEFORE removal, and a failed write keeps the very same tab.
// A file-tree walk failure escaped an async void onto the UI dispatcher.
// No EditorsTabItemControl here: tab.WriteToFile is set directly to a fake writer.
public class MainWindowViewModelDocumentSafetyTests
{
    private static (MainWindowViewModel Vm, Mock<IWindowService> WindowService, Mock<IFileSystemService> FileSystem)
        CreateViewModel(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var windowService = new Mock<IWindowService>();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, windowService, fileSystem);
    }

    private static EditorTabItemViewModel OpenDirtyTab(MainWindowViewModel vm, string path, Action<string> writer)
    {
        vm.OpenTab(path);
        var tab = vm.Tabs.Single(t => t.FilePath == path);
        tab.WriteToFile = writer;
        tab.IsDirty = true;
        return tab;
    }

    private static void ThrowDiskFull(string _) => throw new IOException("disk full");

    // ── Single tab close ──────────────────────────────────────────────────

    [Fact]
    public async Task CloseAsync_Should_WriteADirtyNamedTab_BeforeRemovingIt()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            string? writtenTo = null;
            var tab = OpenDirtyTab(vm, file, p => writtenTo = p);

            await ((IEditorTabActions)vm).CloseAsync(tab);

            Assert.Equal(file, writtenTo);
            Assert.DoesNotContain(tab, vm.Tabs);
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CloseAsync_Should_KeepTheSameDirtyTab_WhenTheWriteFails_AndTheUserCancels()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);
            var tab = OpenDirtyTab(vm, file, ThrowDiskFull);

            await ((IEditorTabActions)vm).CloseAsync(tab);

            Assert.Contains(tab, vm.Tabs);
            Assert.True(tab.IsDirty);
            windowService.Verify(w => w.ShowErrorAsync("Save Failed", It.Is<string>(m => m.Contains("disk full"))), Times.Once);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CloseAsync_Should_CloseTheTab_WhenTheWriteFails_AndTheUserChoosesDontSave()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.DontSave);
            var tab = OpenDirtyTab(vm, file, ThrowDiskFull);

            await ((IEditorTabActions)vm).CloseAsync(tab);

            Assert.DoesNotContain(tab, vm.Tabs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CloseAsync_Should_KeepTheTab_WhenTheWriteFails_AndSaveAsIsCancelled()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Save);
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string?)null);
            var tab = OpenDirtyTab(vm, file, ThrowDiskFull);

            await ((IEditorTabActions)vm).CloseAsync(tab);

            Assert.Contains(tab, vm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.Equal(file, tab.FilePath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A dirty named tab whose editor never wired WriteToFile used to count as
    // "saved" and get closed with its content dropped. It must fail closed instead.
    [Fact]
    public async Task CloseAsync_Should_NotTreatAMissingWriterAsASuccessfulSave()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);
            vm.OpenTab(file);
            var tab = vm.Tabs.Single(t => t.FilePath == file);
            tab.IsDirty = true;
            Assert.Null(tab.WriteToFile);

            await ((IEditorTabActions)vm).CloseAsync(tab);
            await vm.RenameFile(file, "new");

            Assert.Contains(tab, vm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.True(File.Exists(file));
            windowService.Verify(w => w.ShowErrorAsync("Save Failed", It.IsAny<string>()), Times.Once);
            windowService.Verify(w => w.ShowErrorAsync("Rename Failed", It.IsAny<string>()), Times.Once);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── Rename / move / delete ────────────────────────────────────────────

    [Fact]
    public void RenameFile_Should_AbortWithoutTouchingDisk_WhenADirtyTabCannotBeSaved()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "old.rpad");
            File.WriteAllText(file, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            var tab = OpenDirtyTab(vm, file, ThrowDiskFull);

            vm.RenameFile(file, "new");

            Assert.True(File.Exists(file));
            Assert.False(File.Exists(Path.Combine(root, "new.rpad")));
            Assert.Contains(tab, vm.Tabs);
            Assert.True(tab.IsDirty);
            windowService.Verify(w => w.ShowErrorAsync("Rename Failed", It.Is<string>(m => m.Contains("disk full"))), Times.Once);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RenameFile_Should_SaveToTheOldPathFirst_ThenReopenAtTheNewPath()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "old.rpad");
            File.WriteAllText(file, "old");
            var (vm, _, _) = CreateViewModel(root);
            var tab = OpenDirtyTab(vm, file, p => File.WriteAllText(p, "edited"));

            vm.RenameFile(file, "new");

            var newPath = Path.Combine(root, "new.rpad");
            Assert.Equal("edited", File.ReadAllText(newPath));
            Assert.DoesNotContain(tab, vm.Tabs);
            Assert.Contains(vm.Tabs, t => t.FilePath == newPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DeleteFolder_Should_AbortWithoutTouchingDisk_WhenANestedDirtyTabCannotBeSaved()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var folder = Path.Combine(root, "Folder");
            Directory.CreateDirectory(folder);
            var nested = Path.Combine(folder, "a.rpad");
            File.WriteAllText(nested, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            var tab = OpenDirtyTab(vm, nested, ThrowDiskFull);

            vm.DeleteFile(folder);

            Assert.True(File.Exists(nested));
            Assert.Contains(tab, vm.Tabs);
            windowService.Verify(w => w.ShowErrorAsync("Delete Failed", It.IsAny<string>()), Times.Once);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A failed move used to close the tab and reopen it from disk — a new tab instance with the
    // editor state (undo history, unsaved text if the pre-close save had failed) gone.
    [Fact]
    public void MoveFileOrFolder_Should_KeepTheSameTabInstance_WhenTheMoveFails()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var parent = Path.Combine(root, "Parent");
            var child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);
            var nested = Path.Combine(parent, "a.rpad");
            File.WriteAllText(nested, "old");
            var (vm, windowService, _) = CreateViewModel(root);
            var tab = OpenDirtyTab(vm, nested, p => File.WriteAllText(p, "edited"));

            vm.MoveFileOrFolder(parent, child); // into its own descendant — Directory.Move throws

            windowService.Verify(w => w.ShowErrorAsync("Move Failed", It.IsAny<string>()), Times.Once);
            Assert.Contains(tab, vm.Tabs);
            Assert.Equal(nested, tab.FilePath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OpenTab_Should_NotCloseADirtyInitialUntitledTab()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");
            var (vm, _, _) = CreateViewModel(root);
            vm.NewRpadFile();
            var untitled = Assert.Single(vm.Tabs);
            untitled.IsDirty = true;

            vm.OpenTab(file);

            Assert.Contains(untitled, vm.Tabs);
            Assert.Equal(2, vm.Tabs.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── File tree refresh ─────────────────────────────────────────────────

    [Fact]
    public async Task RefreshNodesAsync_Should_KeepThePreviousTree_AndReportOnce_WhenTheWalkFails()
    {
        var (vm, windowService, fileSystem) = CreateViewModel("/root");
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);
        await vm.RefreshNodesAsync();

        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("denied"));
        await vm.RefreshNodesAsync();
        await vm.RefreshNodesAsync();

        Assert.Single(vm.FileTree.Nodes, n => n.FilePath == "/root/a.rpad");
        windowService.Verify(w => w.ShowErrorAsync("Folder Unavailable", It.Is<string>(m => m.Contains("denied"))), Times.Once);

        // A successful refresh re-arms the report for the next failure.
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await vm.RefreshNodesAsync();
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("gone"));
        await vm.RefreshNodesAsync();
        windowService.Verify(w => w.ShowErrorAsync("Folder Unavailable", It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RefreshNodesAsync_Should_NotReportCancellation()
    {
        var (vm, windowService, fileSystem) = CreateViewModel("/root");
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await vm.RefreshNodesAsync();

        windowService.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RefreshNodesAsync_Should_NotLetAnOlderWalkOverwriteANewerTree()
    {
        var (vm, _, fileSystem) = CreateViewModel("/root");
        var older = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>();
        var newer = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>();
        var pending = new Queue<Task<IReadOnlyList<FileSystemItem>>>([older.Task, newer.Task]);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).Returns(() => pending.Dequeue());

        var olderRefresh = vm.RefreshNodesAsync();
        var newerRefresh = vm.RefreshNodesAsync();
        newer.SetResult([new FileSystemItem("new.rpad", false, "/root/new.rpad", null)]);
        await newerRefresh;
        older.SetResult([new FileSystemItem("old.rpad", false, "/root/old.rpad", null)]);
        await olderRefresh;

        var node = Assert.Single(vm.FileTree.Nodes);
        Assert.Equal("/root/new.rpad", node.FilePath);
    }

    // Comparing against the last APPLIED version let an older walk land after a
    // newer walk had failed (and deliberately kept the previous tree) — resurrecting stale content.
    [Fact]
    public async Task RefreshNodesAsync_Should_KeepThePreviousTree_WhenANewerWalkFailed_AndAnOlderOneFinishesLate()
    {
        var (vm, _, fileSystem) = CreateViewModel("/root");
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FileSystemItem("current.rpad", false, "/root/current.rpad", null)]);
        await vm.RefreshNodesAsync();

        var older = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>();
        var pending = new Queue<Task<IReadOnlyList<FileSystemItem>>>(
            [older.Task, Task.FromException<IReadOnlyList<FileSystemItem>>(new IOException("gone"))]);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).Returns(() => pending.Dequeue());

        var olderRefresh = vm.RefreshNodesAsync();
        await vm.RefreshNodesAsync(); // newer one fails
        older.SetResult([new FileSystemItem("stale.rpad", false, "/root/stale.rpad", null)]);
        await olderRefresh;

        var node = Assert.Single(vm.FileTree.Nodes);
        Assert.Equal("/root/current.rpad", node.FilePath);
    }

    [Fact]
    public async Task RefreshNodesAsync_Should_NotTouchTheTree_AfterDispose()
    {
        var (vm, windowService, fileSystem) = CreateViewModel("/root");
        var pending = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>();
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);

        var refresh = vm.RefreshNodesAsync();
        vm.Dispose();
        pending.SetResult([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);
        await refresh;

        Assert.Empty(vm.FileTree.Nodes);
        windowService.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
