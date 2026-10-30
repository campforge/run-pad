using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

// FindByPath is used right after creating a new folder to locate its freshly-merged
// node and enter rename mode on it immediately (VS/Rider/Explorer-style) — see
// FileTreeView.StartRenameForPath / MainWindow.FileTree_CreateFolder.
public class FileTreeViewModelTests
{
    // FileTreeViewModel now reads RenameFile/DeleteFile gestures from IPreferencesService
    // (routed through here since FileTreeView itself has no DI) — a plain KeyboardSettings default is
    // enough for these tests, none of which touch RenameGesture/DeleteGesture directly.
    // Also needs ILanguageHostRegistry (per-language file icon) — a real registry with
    // both real factories registered (not mocked), same precedent as
    // PreferencesWindowViewModelTests.CreateRealRegistry, so LanguageIconKey resolution below is
    // exercised end to end rather than assuming what a mock would return.
    private static FileTreeViewModel CreateViewModel()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));
        return new FileTreeViewModel(prefs.Object, registry);
    }

    [Fact]
    public void FindByPath_Should_FindATopLevelNode()
    {
        var vm = CreateViewModel();
        vm.Refresh([new FileSystemItem("a.inv", false, "/root/a.inv", null)]);

        var found = vm.FindByPath("/root/a.inv");

        Assert.NotNull(found);
        Assert.Equal("/root/a.inv", found!.FilePath);
    }

    [Fact]
    public void FindByPath_Should_FindANestedNode_InsideAFolder()
    {
        var vm = CreateViewModel();
        vm.Refresh([
            new FileSystemItem("Sub", true, "/root/Sub", [
                new FileSystemItem("b.inv", false, "/root/Sub/b.inv", null),
            ]),
        ]);

        var found = vm.FindByPath("/root/Sub/b.inv");

        Assert.NotNull(found);
        Assert.Equal("/root/Sub/b.inv", found!.FilePath);
    }

    [Fact]
    public void FindByPath_Should_ReturnNull_WhenThePathIsNotInTheTree()
    {
        var vm = CreateViewModel();
        vm.Refresh([new FileSystemItem("a.inv", false, "/root/a.inv", null)]);

        Assert.Null(vm.FindByPath("/root/missing.inv"));
    }

    // MapToNode peeks each file's language from its real front matter (not just from the
    // FileSystemItem record) — these need actual files on disk, unlike the path-only tests above.
    [Fact]
    public void Refresh_Should_SetIsFolder_ForADirectoryNode_AndNotForAFileNode()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filetree-test").FullName;
        try
        {
            var sub = Path.Combine(root, "Sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(root, "a.inv");
            File.WriteAllText(file, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));

            var vm = CreateViewModel();
            vm.Refresh([
                new FileSystemItem("Sub", true, sub, []),
                new FileSystemItem("a.inv", false, file, null),
            ]);

            Assert.True(vm.FindByPath(sub)!.IsFolder);
            Assert.False(vm.FindByPath(file)!.IsFolder);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Refresh_Should_SetLanguage_FromTheFilesOwnFrontMatter()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filetree-test").FullName;
        try
        {
            var csharpFile = Path.Combine(root, "cs.inv");
            File.WriteAllText(csharpFile, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "CSharp Demo"));
            var vbFile = Path.Combine(root, "vb.inv");
            File.WriteAllText(vbFile,
                "---\nversion: \"1.0\"\nlanguage: vb\ntargetSdk: net10.0\nkind: Statements\n---\n\n# VB Demo\n\n```vb run\nConsole.WriteLine(\"hi\")\n```\n");

            var vm = CreateViewModel();
            vm.Refresh([
                new FileSystemItem("cs.inv", false, csharpFile, null),
                new FileSystemItem("vb.inv", false, vbFile, null),
            ]);

            // Both languages have a real Material Design icon today (C# gets its own
            // language-csharp icon; VB stands in with the Visual Studio logo, the user's choice
            // since Material Design Icons has no per-language VB icon at all) — neither falls back
            // to the tree's generic file icon.
            var csNode = vm.FindByPath(csharpFile)!;
            Assert.Equal(MdRunLanguage.CSharp, csNode.Language);
            Assert.Equal("Icon.LanguageCSharp", csNode.LanguageIconKey);
            Assert.False(csNode.ShowGenericFileIcon);

            var vbNode = vm.FindByPath(vbFile)!;
            Assert.Equal(MdRunLanguage.Vb, vbNode.Language);
            Assert.Equal("Icon.LanguageVb", vbNode.LanguageIconKey);
            Assert.False(vbNode.ShowGenericFileIcon);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ShowGenericFileIcon is a plain computed property (!IsFolder && LanguageIconKey is null) — this
    // guards just that, independent of how LanguageIconKey got resolved.
    [Fact]
    public void ShowGenericFileIcon_Should_BeTrue_WhenLanguageIconKeyIsNull()
    {
        var node = new FileTreeViewNode("x.inv", "/root/x.inv", MdRunLanguage.Vb) { LanguageIconKey = null };

        Assert.True(node.ShowGenericFileIcon);
    }

    // MapFileNode's actual fallback — a language whose id isn't registered in
    // ILanguageHostRegistry at all (any future MdRunLanguage with no ILanguageHostFactory yet) — falls
    // back to the generic file icon instead of throwing (GetEditorProfile throws for an unregistered
    // id, hence the IsRegistered guard in FileTreeViewModel.MapFileNode). Registers only C# here to
    // simulate "vb" being such a language, rather than inventing an impossible MdRunLanguage value —
    // MdRunParser.TryParse already normalizes MdRunLanguage.Unknown to CSharp before this code ever
    // sees it (MdRunParser.cs:54), so Unknown itself can't reach MapFileNode from a real file.
    [Fact]
    public void Refresh_Should_FallBackToGenericIcon_ForALanguageNotRegisteredInTheRegistry()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-filetree-test").FullName;
        try
        {
            var vbFile = Path.Combine(root, "vb.inv");
            File.WriteAllText(vbFile,
                "---\nversion: \"1.0\"\nlanguage: vb\ntargetSdk: net10.0\nkind: Statements\n---\n\n# VB Demo\n\n```vb run\nConsole.WriteLine(\"hi\")\n```\n");

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            var registry = new LanguageHostRegistry();
            registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
            var vm = new FileTreeViewModel(prefs.Object, registry);

            vm.Refresh([new FileSystemItem("vb.inv", false, vbFile, null)]);

            var vbNode = vm.FindByPath(vbFile)!;
            Assert.Equal(MdRunLanguage.Vb, vbNode.Language);
            Assert.Null(vbNode.LanguageIconKey);
            Assert.True(vbNode.ShowGenericFileIcon);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
