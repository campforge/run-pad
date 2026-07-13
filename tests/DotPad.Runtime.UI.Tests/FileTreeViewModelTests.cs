using DotPad.Services;
using DotPad.UserControls.Models;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #69: FindByPath is used right after creating a new folder to locate its freshly-merged
// node and enter rename mode on it immediately (VS/Rider/Explorer-style) — see
// FileTreeView.StartRenameForPath / MainWindow.FileTree_CreateFolder.
public class FileTreeViewModelTests
{
    [Fact]
    public void FindByPath_Should_FindATopLevelNode()
    {
        var vm = new FileTreeViewModel();
        vm.Refresh([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);

        var found = vm.FindByPath("/root/a.rpad");

        Assert.NotNull(found);
        Assert.Equal("/root/a.rpad", found!.FilePath);
    }

    [Fact]
    public void FindByPath_Should_FindANestedNode_InsideAFolder()
    {
        var vm = new FileTreeViewModel();
        vm.Refresh([
            new FileSystemItem("Sub", true, "/root/Sub", [
                new FileSystemItem("b.rpad", false, "/root/Sub/b.rpad", null),
            ]),
        ]);

        var found = vm.FindByPath("/root/Sub/b.rpad");

        Assert.NotNull(found);
        Assert.Equal("/root/Sub/b.rpad", found!.FilePath);
    }

    [Fact]
    public void FindByPath_Should_ReturnNull_WhenThePathIsNotInTheTree()
    {
        var vm = new FileTreeViewModel();
        vm.Refresh([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);

        Assert.Null(vm.FindByPath("/root/missing.rpad"));
    }
}
