using Moq;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Examples;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The Examples panel: sample scripts bundled in the application, opened as untitled, unsaved copies —
// never run, never written to disk, one tab per example.
public class ExampleScriptsTests
{
    // ── The bundled set ──────────────────────────────────────────────────────

    [Fact]
    public void TheBundledExamples_Should_BeTheStarterSet_InOrder_WithReadableTitles()
    {
        var examples = ExampleScripts.All;

        Assert.Equal(
            ["quick-start.rpad", "expression-kind.rpad", "program-kind.rpad", "products-overview.rpad",
             "live-output.rpad", "debugging-loop.rpad"],
            examples.Select(e => e.Id));
        Assert.Equal(
            ["Quick start", "Expression kind", "Program kind", "Products overview", "Live output", "Debugging loop"],
            examples.Select(e => e.Title));
        Assert.DoesNotContain(examples, e => e.Id.Contains("analyzer", StringComparison.Ordinal)); // the broken-on-purpose demo
    }

    [Fact]
    public void EveryExample_Should_BeAValidCSharpScript_OfItsDeclaredKind()
    {
        foreach (var example in ExampleScripts.All)
        {
            var parsed = RpadParser.Parse(example.Text);
            Assert.Null(parsed.Error);
            var document = Assert.IsType<RpadDocument>(parsed.Document);
            Assert.Equal(RpadLanguage.CSharp, example.Language);
            Assert.Equal(document.FrontMatter.Kind, example.Kind);
            Assert.False(string.IsNullOrWhiteSpace(document.RunCode), example.Id);
        }
        Assert.Contains(ExampleScripts.All, e => e.Kind == RpadKind.Expression);
        Assert.Contains(ExampleScripts.All, e => e.Kind == RpadKind.Program);
    }

    // An assembly without example resources (or with unreadable ones) gives an empty list, not an error.
    [Fact]
    public void WithoutResources_Should_GiveNoExamples()
        => Assert.Empty(ExampleScripts.Load(typeof(ExampleScriptsTests).Assembly));

    [Theory]
    [InlineData("---\nkind: Statements\n---\n\n# My sample\n\ntext", "x.rpad", "My sample")]
    [InlineData("---\nkind: Statements\n---\n\nno heading", "first-steps.rpad", "First steps")]
    public void TitleOf_Should_UseTheFirstHeading_ElseTheFileName(string text, string id, string expected)
        => Assert.Equal(expected, ExampleScripts.TitleOf(text, id));

    // ── Opening ──────────────────────────────────────────────────────────────

    private static (MainWindowViewModel Vm, Mock<IFileSystemService> FileSystem, string Folder) CreateViewModel()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-examples-test").FullName;
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
        return (vm, fileSystem, folder);
    }

    [Fact]
    public void OpeningAnExample_Should_OpenAnUntitledCopy_WithoutTouchingTheQueriesFolder()
    {
        var (vm, fileSystem, folder) = CreateViewModel();
        try
        {
            var example = vm.Examples.Single(e => e.Kind == RpadKind.Program);

            vm.OpenExample(example);

            var tab = vm.ActiveTab!;
            Assert.Null(tab.FilePath);
            Assert.Same(example, tab.Example);
            Assert.Equal("Program kind", tab.FileName); // the tab title, and Save As's suggested name
            Assert.Equal(RpadKind.Program, tab.NewFileKind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            fileSystem.Verify(f => f.CreateNewFile(It.IsAny<string>(), It.IsAny<RpadLanguage>(), It.IsAny<RpadKind>(), It.IsAny<string>()),
                Times.Never);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // The same example again: its open untitled tab comes to the front, no second copy.
    [Fact]
    public void OpeningTheSameExampleTwice_Should_ReuseItsTab()
    {
        var (vm, _, folder) = CreateViewModel();
        try
        {
            var quickStart = vm.Examples[0];
            vm.OpenExample(quickStart);
            var tab = vm.ActiveTab;
            vm.OpenExample(vm.Examples[1]);
            var count = vm.Tabs.Count;

            vm.OpenExample(quickStart);

            Assert.Same(tab, vm.ActiveTab);
            Assert.Equal(count, vm.Tabs.Count);
            Assert.Single(vm.Tabs, t => t.Example == quickStart);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheSamplesBundle_Should_NotShowTheNoExamplesHint_WhenThereAreExamples()
    {
        var (vm, _, folder) = CreateViewModel();
        try
        {
            Assert.False(vm.ShowNoExamplesHint);
            Assert.Null(vm.ConnectionNavigation); // no store given: the Connections bundle is hidden
            Assert.False(vm.HasConnectionNavigation);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
