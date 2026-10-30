using Invoklet.Format.MdRun;
using Invoklet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

// GetFilesTreeInternal used to filter out any folder whose subtree
// contained zero .inv files ("if (children.Count > 0)") — meant to declutter folders with no scripts
// in them, but it silently ate a folder the moment it had no content yet, which is exactly the shape
// of a folder the new "New Folder" command just created. The user hit this immediately
// after that feature shipped: the directory really was created on disk, but RefreshNodes() never
// showed it, so there was nothing for FindByPath/StartRenameForPath to find either.
public class FileSystemServiceTests
{
    [Fact]
    public void GetFilesTreeInternal_Should_IncludeAnEmptyFolder()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
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
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
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
    public void GetFilesTreeInternal_Should_StillIncludeAFolderWithMdRunFilesInIt()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.inv"), "");

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            var folder = Assert.Single(tree, i => i is { IsDirectory: true, Name: "Sub" });
            Assert.Contains(folder.Children!, i => i is { IsDirectory: false, Name: "a.inv" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // File tree's "New File" — CreateNewFile writes real, parseable .inv content (via
    // MdRunWriter.NewFile) into a caller-given directory, not just an empty stub. NullLogger avoids
    // wiring up a real Microsoft.Extensions.Logging pipeline just to construct FileSystemService —
    // the constructor's only side effect (see GetOrCreateDefaultFolder) is ensuring the real
    // "Documents/Invoklet Queries" folder exists, same as happens on every normal app launch; it's
    // never written into by these tests, which always pass their own temp directory as parentDir.
    [Fact]
    public void CreateNewFile_Should_WriteAParseableMdRunDocument_WithTheRequestedLanguageAndKind()
    {
        var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var dir = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            var path = service.CreateNewFile(dir, MdRunLanguage.Vb, MdRunKind.Program, "");

            Assert.True(File.Exists(path));
            var doc = MdRunParser.TryParse(File.ReadAllText(path));
            Assert.NotNull(doc);
            Assert.Equal(MdRunLanguage.Vb, doc!.FrontMatter.Language);
            Assert.Equal(MdRunKind.Program, doc.FrontMatter.Kind);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CreateNewFile_Should_ProbeForAFreeName_WhenTheFirstCandidateIsTaken()
    {
        var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var dir = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Query 1.inv"), "");

            var path = service.CreateNewFile(dir, MdRunLanguage.CSharp, MdRunKind.Statements, "");

            Assert.Equal(Path.Combine(dir, "Query 2.inv"), path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The token used to only gate Task.Run scheduling — the walk itself ignored it.
    [Fact]
    public void GetFilesTreeInternal_Should_ThrowOperationCanceled_WhenTheTokenIsCancelled()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
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

        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        var locked = Path.Combine(root, "Locked");
        try
        {
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "hidden.inv"), "");
            Directory.CreateDirectory(Path.Combine(root, "Open"));
            File.WriteAllText(Path.Combine(root, "Open", "b.inv"), "");
            File.WriteAllText(Path.Combine(root, "a.inv"), "");
            File.SetUnixFileMode(locked, UnixFileMode.None);

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            var lockedNode = Assert.Single(tree, i => i.Name == "Locked");
            Assert.Empty(lockedNode.Children!);
            var openNode = Assert.Single(tree, i => i.Name == "Open");
            Assert.Single(openNode.Children!, i => i.Name == "b.inv");
            Assert.Contains(tree, i => i.Name == "a.inv");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    // A queries folder that is also an Obsidian vault (or a Git repository) has .obsidian/.git
    // folders full of tool files: folders whose own name starts with a dot are not listed, at any
    // depth, and nothing inside them is walked. Files keep their current rule (*.inv, any name).
    [Fact]
    public void GetFilesTreeInternal_Should_HideDotFolders_AtEveryDepth_AndKeepEverythingElse()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".obsidian", "plugins"));
            File.WriteAllText(Path.Combine(root, ".obsidian", "inside.inv"), "");
            File.WriteAllText(Path.Combine(root, ".obsidian", "plugins", "deep.inv"), "");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "Notes", ".trash"));
            File.WriteAllText(Path.Combine(root, "Notes", ".trash", "old.inv"), "");
            File.WriteAllText(Path.Combine(root, "Notes", "kept.inv"), "");
            Directory.CreateDirectory(Path.Combine(root, "Empty"));
            Directory.CreateDirectory(Path.Combine(root, "project.v2"));
            File.WriteAllText(Path.Combine(root, "project.v2", "script.inv"), "");
            File.WriteAllText(Path.Combine(root, ".dotted.inv"), "");

            var tree = FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken);

            Assert.Equal([".dotted.inv", "Empty", "Notes", "project.v2"], tree.Select(i => i.Name).Order(StringComparer.Ordinal));
            var notes = tree.Single(i => i.Name == "Notes");
            Assert.Equal(["kept.inv"], notes.Children!.Select(i => i.Name));
            Assert.Equal(["script.inv"], tree.Single(i => i.Name == "project.v2").Children!.Select(i => i.Name));
            Assert.DoesNotContain(Flatten(tree), i => i.FilePath.Contains(".obsidian") || i.FilePath.Contains(".git") || i.FilePath.Contains(".trash"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The folder the user chose is listed from even when its own name (or a parent's) starts with a
    // dot — only folders below it are judged by their names.
    [Fact]
    public void GetFilesTreeInternal_Should_ListADotNamedRootFolder()
    {
        var parent = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            var root = Path.Combine(parent, ".hidden-parent", ".queries");
            Directory.CreateDirectory(Path.Combine(root, "Scripts"));
            File.WriteAllText(Path.Combine(root, "Scripts", "a.inv"), "");
            Directory.CreateDirectory(Path.Combine(root, ".cache"));

            var tree = FileSystemService.GetFilesTreeInternal(root + Path.DirectorySeparatorChar, ct: TestContext.Current.CancellationToken);

            var scripts = Assert.Single(tree);
            Assert.Equal("Scripts", scripts.Name);
            Assert.Equal(["a.inv"], scripts.Children!.Select(i => i.Name));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // Renaming a folder to a dot name hides it on the next refresh, and back shows it again.
    [Fact]
    public void GetFilesTreeInternal_Should_FollowRenamesIntoAndOutOfADotName()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Drafts"));
            File.WriteAllText(Path.Combine(root, "Drafts", "d.inv"), "");
            List<string> Names() => [.. FileSystemService.GetFilesTreeInternal(root, ct: TestContext.Current.CancellationToken).Select(i => i.Name)];

            Assert.Equal(["Drafts"], Names());
            Directory.Move(Path.Combine(root, "Drafts"), Path.Combine(root, ".Drafts"));
            Assert.Empty(Names());
            Directory.Move(Path.Combine(root, ".Drafts"), Path.Combine(root, "Drafts"));
            Assert.Equal(["Drafts"], Names());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(".obsidian", true)]
    [InlineData(".git/", true)]
    [InlineData("project.v2", false)]
    [InlineData("Notes", false)]
    [InlineData("a.b", false)]
    public void IsDotDirectory_Should_JudgeTheFoldersOwnName(string name, bool expected)
    {
        Assert.Equal(expected, FileSystemService.IsDotDirectory(Path.Combine(Path.GetTempPath(), "parent.d", name)));
    }

    private static IEnumerable<FileSystemItem> Flatten(IEnumerable<FileSystemItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children ?? [])));
}
