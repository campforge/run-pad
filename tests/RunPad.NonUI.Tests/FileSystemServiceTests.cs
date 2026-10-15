using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Format.RPad;
using RunPad.Services;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// GetFilesTreeInternal used to filter out any folder whose subtree
// contained zero .rpad files ("if (children.Count > 0)") — meant to declutter folders with no scripts
// in them, but it silently ate a folder the moment it had no content yet, which is exactly the shape
// of a folder the new "New Folder" command just created. The user hit this immediately
// after that feature shipped: the directory really was created on disk, but RefreshNodes() never
// showed it, so there was nothing for FindByPath/StartRenameForPath to find either.
public class FileSystemServiceTests
{
    [Fact]
    public void GetFilesTreeInternal_Should_IncludeAnEmptyFolder()
    {
        var root = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "New Folder"));

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            Assert.Contains(tree, i => i is { IsDirectory: true, Name: "New Folder" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetFilesTreeInternal_Should_IncludeAnEmptyFolder_NestedInsideAnotherFolder()
    {
        var root = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Parent", "Empty Child"));

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            var parent = Assert.Single(tree, i => i is { IsDirectory: true, Name: "Parent" });
            Assert.Contains(parent.Children!, i => i is { IsDirectory: true, Name: "Empty Child" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetFilesTreeInternal_Should_StillIncludeAFolderWithRpadFilesInIt()
    {
        var root = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.rpad"), "");

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            var folder = Assert.Single(tree, i => i is { IsDirectory: true, Name: "Sub" });
            Assert.Contains(folder.Children!, i => i is { IsDirectory: false, Name: "a.rpad" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // File tree's "New File" — CreateNewFile writes real, parseable .rpad content (via
    // RpadWriter.NewFile) into a caller-given directory, not just an empty stub. NullLogger avoids
    // wiring up a real Microsoft.Extensions.Logging pipeline just to construct FileSystemService —
    // the constructor's only side effect (see GetOrCreateDefaultFolder) is ensuring the real
    // "Documents/RunPad Queries" folder exists, same as happens on every normal app launch; it's
    // never written into by these tests, which always pass their own temp directory as parentDir.
    [Fact]
    public void CreateNewFile_Should_WriteAParseableRpadDocument_WithTheRequestedLanguageAndKind()
    {
        var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var dir = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            var path = service.CreateNewFile(dir, RpadLanguage.Vb, RpadKind.Program, "");

            Assert.True(File.Exists(path));
            var doc = RpadParser.TryParse(File.ReadAllText(path));
            Assert.NotNull(doc);
            Assert.Equal(RpadLanguage.Vb, doc!.FrontMatter.Language);
            Assert.Equal(RpadKind.Program, doc.FrontMatter.Kind);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CreateNewFile_Should_ProbeForAFreeName_WhenTheFirstCandidateIsTaken()
    {
        var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var dir = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Query 1.rpad"), "");

            var path = service.CreateNewFile(dir, RpadLanguage.CSharp, RpadKind.Statements, "");

            Assert.Equal(Path.Combine(dir, "Query 2.rpad"), path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The token used to only gate Task.Run scheduling — the walk itself ignored it.
    [Fact]
    public void GetFilesTreeInternal_Should_ThrowOperationCanceled_WhenTheTokenIsCancelled()
    {
        var root = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Child"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => FileSystemService.GetFilesTreeInternal(root, logger: null, cts.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // One unreadable subfolder used to abort the whole walk (and, through the
    // fire-and-forget refresh, crash the app). It's now listed without children, siblings intact.
    [Fact]
    public void GetFilesTreeInternal_Should_KeepWalking_WhenASubfolderIsUnreadable()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var root = Directory.CreateTempSubdirectory("runpad-filesystem-test").FullName;
        var locked = Path.Combine(root, "Locked");
        try
        {
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "hidden.rpad"), "");
            Directory.CreateDirectory(Path.Combine(root, "Open"));
            File.WriteAllText(Path.Combine(root, "Open", "b.rpad"), "");
            File.WriteAllText(Path.Combine(root, "a.rpad"), "");
            File.SetUnixFileMode(locked, UnixFileMode.None);

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            var lockedNode = Assert.Single(tree, i => i.Name == "Locked");
            Assert.Empty(lockedNode.Children!);
            var openNode = Assert.Single(tree, i => i.Name == "Open");
            Assert.Single(openNode.Children!, i => i.Name == "b.rpad");
            Assert.Contains(tree, i => i.Name == "a.rpad");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }
}
