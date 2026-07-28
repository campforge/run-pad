using Moq;
using RunPad.Contracts;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #69 follow-up: user reported live that Delete and Rename both silently failed for
// a folder in the file tree. Root cause — DeleteFile/RenameFile always called File.Delete/File.Move,
// which throw for a directory path (File.Move explicitly, File.Delete via the OS); the delete's
// catch swallowed the exception entirely (so the tree acted like the folder was gone while it was
// still on disk), and the rename's catch showed an error but never actually renamed anything.
public class MainWindowViewModelDeleteRenameFolderTests
{
    private static MainWindowViewModel CreateViewModel(string folder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        return new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
    }

    [Fact]
    public void DeleteFile_Should_DeleteADirectory_Recursively()
    {
        var root = Directory.CreateTempSubdirectory("runpad-delete-test").FullName;
        try
        {
            var sub = Path.Combine(root, "ToDelete");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.rpad"), "content");

            var vm = CreateViewModel(root);
            vm.DeleteFile(sub);

            Assert.False(Directory.Exists(sub));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DeleteFile_Should_StillDeleteAPlainFile()
    {
        var root = Directory.CreateTempSubdirectory("runpad-delete-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");

            var vm = CreateViewModel(root);
            vm.DeleteFile(file);

            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Code-review finding on the #69/#72-era PR: DeleteFile closed the tab before attempting the
    // delete but never reopened it on failure (unlike RenameFile, which already did) — a failed
    // delete could make a tab disappear even though the file was still on disk. Forces a real delete
    // failure (Unix permission trick, same precedent as SaveIfNeededFailureTests) rather than mocking
    // File/Directory statically, since there's no IFileSystemService seam for delete itself.
    [Fact]
    public void DeleteFile_Should_ReopenTheTab_WhenTheDeleteFails()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var root = Directory.CreateTempSubdirectory("runpad-delete-fail-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, "content");

            var vm = CreateViewModel(root);
            vm.OpenTab(file);
            Assert.Contains(vm.Tabs, t => t.FilePath == file);

            // Deleting a file requires write permission on its PARENT directory on Unix (the file's
            // own permissions don't matter) — stripping it makes File.Delete throw deterministically.
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                vm.DeleteFile(file);
            }
            finally
            {
                File.SetUnixFileMode(root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.True(File.Exists(file));
            Assert.Contains(vm.Tabs, t => t.FilePath == file);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RenameFile_Should_RenameADirectory()
    {
        var root = Directory.CreateTempSubdirectory("runpad-rename-test").FullName;
        try
        {
            var sub = Path.Combine(root, "OldName");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.rpad"), "content");

            var vm = CreateViewModel(root);
            vm.RenameFile(sub, "NewName");

            Assert.False(Directory.Exists(sub));
            Assert.True(Directory.Exists(Path.Combine(root, "NewName")));
            Assert.True(File.Exists(Path.Combine(root, "NewName", "a.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RenameFile_Should_StillRenameAPlainFile()
    {
        var root = Directory.CreateTempSubdirectory("runpad-rename-test").FullName;
        try
        {
            var file = Path.Combine(root, "old.rpad");
            File.WriteAllText(file, "content");

            var vm = CreateViewModel(root);
            vm.RenameFile(file, "new");

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(root, "new.rpad")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
