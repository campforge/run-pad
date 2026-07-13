using DotPad.Services;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #69 follow-up: GetFilesTreeInternal used to filter out any folder whose subtree
// contained zero .rpad files ("if (children.Count > 0)") — meant to declutter folders with no scripts
// in them, but it silently ate a folder the moment it had no content yet, which is exactly the shape
// of a folder the new "New Folder" command (TODO #69) just created. The user hit this immediately
// after that feature shipped: the directory really was created on disk, but RefreshNodes() never
// showed it, so there was nothing for FindByPath/StartRenameForPath to find either.
public class FileSystemServiceTests
{
    [Fact]
    public void GetFilesTreeInternal_Should_IncludeAnEmptyFolder()
    {
        var root = Directory.CreateTempSubdirectory("dotpad-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "New Folder"));

            var tree = FileSystemService.GetFilesTreeInternal(root);

            Assert.Contains(tree, i => i is { IsDirectory: true, Name: "New Folder" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetFilesTreeInternal_Should_IncludeAnEmptyFolder_NestedInsideAnotherFolder()
    {
        var root = Directory.CreateTempSubdirectory("dotpad-filesystem-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Parent", "Empty Child"));

            var tree = FileSystemService.GetFilesTreeInternal(root);

            var parent = Assert.Single(tree, i => i is { IsDirectory: true, Name: "Parent" });
            Assert.Contains(parent.Children!, i => i is { IsDirectory: true, Name: "Empty Child" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetFilesTreeInternal_Should_StillIncludeAFolderWithRpadFilesInIt()
    {
        var root = Directory.CreateTempSubdirectory("dotpad-filesystem-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.rpad"), "");

            var tree = FileSystemService.GetFilesTreeInternal(root);

            var folder = Assert.Single(tree, i => i is { IsDirectory: true, Name: "Sub" });
            Assert.Contains(folder.Children!, i => i is { IsDirectory: false, Name: "a.rpad" });
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
