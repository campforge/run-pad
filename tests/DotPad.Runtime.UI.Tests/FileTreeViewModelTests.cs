using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #69: FindByPath is used right after creating a new folder to locate its freshly-merged
// node and enter rename mode on it immediately (VS/Rider/Explorer-style) — see
// FileTreeView.StartRenameForPath / MainWindow.FileTree_CreateFolder.
public class FileTreeViewModelTests
{
    // TODO.md #80: FileTreeViewModel now reads RenameFile/DeleteFile gestures from IPreferencesService
    // (routed through here since FileTreeView itself has no DI) — a plain KeyboardSettings default is
    // enough for these tests, none of which touch RenameGesture/DeleteGesture directly.
    private static FileTreeViewModel CreateViewModel()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        return new FileTreeViewModel(prefs.Object);
    }

    [Fact]
    public void FindByPath_Should_FindATopLevelNode()
    {
        var vm = CreateViewModel();
        vm.Refresh([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);

        var found = vm.FindByPath("/root/a.rpad");

        Assert.NotNull(found);
        Assert.Equal("/root/a.rpad", found!.FilePath);
    }

    [Fact]
    public void FindByPath_Should_FindANestedNode_InsideAFolder()
    {
        var vm = CreateViewModel();
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
        var vm = CreateViewModel();
        vm.Refresh([new FileSystemItem("a.rpad", false, "/root/a.rpad", null)]);

        Assert.Null(vm.FindByPath("/root/missing.rpad"));
    }

    // TODO.md #70: MapToNode peeks each file's language from its real front matter (not just from the
    // FileSystemItem record) — these need actual files on disk, unlike the path-only tests above.
    [Fact]
    public void Refresh_Should_SetIsFolder_ForADirectoryNode_AndNotForAFileNode()
    {
        var root = Directory.CreateTempSubdirectory("dotpad-filetree-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(root, "a.rpad");
            File.WriteAllText(file, RpadWriter.NewFile("Demo"));

            var vm = CreateViewModel();
            vm.Refresh([
                new FileSystemItem("Sub", true, sub, []),
                new FileSystemItem("a.rpad", false, file, null),
            ]);

            Assert.True(vm.FindByPath(sub)!.IsFolder);
            Assert.False(vm.FindByPath(file)!.IsFolder);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Refresh_Should_SetLanguage_FromTheFilesOwnFrontMatter()
    {
        var root = Directory.CreateTempSubdirectory("dotpad-filetree-test").FullName;
        try
        {
            var csharpFile = Path.Combine(root, "cs.rpad");
            File.WriteAllText(csharpFile, RpadWriter.NewFile("CSharp Demo"));
            var vbFile = Path.Combine(root, "vb.rpad");
            File.WriteAllText(vbFile,
                "---\nversion: \"1.0\"\nlanguage: vb\ntargetSdk: net10.0\nkind: Statements\n---\n\n# VB Demo\n\n```vb run\nConsole.WriteLine(\"hi\")\n```\n");

            var vm = CreateViewModel();
            vm.Refresh([
                new FileSystemItem("cs.rpad", false, csharpFile, null),
                new FileSystemItem("vb.rpad", false, vbFile, null),
            ]);

            // Both languages have a real Material Design icon today (C# gets its own
            // language-csharp icon; VB stands in with the Visual Studio logo, the user's choice
            // since Material Design Icons has no per-language VB icon at all) — neither falls back
            // to the tree's generic file icon.
            var csNode = vm.FindByPath(csharpFile)!;
            Assert.Equal(RpadLanguage.CSharp, csNode.Language);
            Assert.Equal("Icon.LanguageCSharp", csNode.LanguageIconKey);
            Assert.False(csNode.ShowGenericFileIcon);

            var vbNode = vm.FindByPath(vbFile)!;
            Assert.Equal(RpadLanguage.Vb, vbNode.Language);
            Assert.Equal("Icon.LanguageVb", vbNode.LanguageIconKey);
            Assert.False(vbNode.ShowGenericFileIcon);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Both known languages have an icon today, so this exercises the fallback branch directly on the
    // node (no file I/O needed) — the whole point of LanguageIconKey/ShowGenericFileIcon being a
    // generic mechanism rather than one flag per language is that a language with no icon entry yet
    // (RpadLanguage.Unknown here, standing in for "some future language") isn't a special case.
    [Fact]
    public void ShowGenericFileIcon_Should_BeTrue_ForALanguageWithNoIconEntry()
    {
        var node = new FileTreeViewNode("x.rpad", "/root/x.rpad", RpadLanguage.Unknown);

        Assert.Null(node.LanguageIconKey);
        Assert.True(node.ShowGenericFileIcon);
    }
}
