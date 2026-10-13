using Avalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.VB;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using TextMateSharp.Grammars;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The Languages settings tree used to be two hardcoded SettingsNodes
// ("languages.csharp"/"languages.vb") instead of derived from ILanguageHostRegistry.SupportedLanguages
// — a third registered language would never show up here without editing this file directly. Uses a
// real LanguageHostRegistry + real CSharpLanguageHostFactory/VbLanguageHostFactory (not mocked), same
// precedent as EditorTabSnippetsTests.cs, so the DisplayName wiring is exercised end to end.
public class PreferencesWindowViewModelTests
{
    private static PreferencesWindowViewModel CreateViewModel(ILanguageHostRegistry registry)
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        return new PreferencesWindowViewModel(theme.Object, prefs.Object, registry);
    }

    private static LanguageHostRegistry CreateRealRegistry()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(loggerFactory));
        registry.Register(new VbLanguageHostFactory(loggerFactory));
        return registry;
    }

    // These 4 properties are newly wired (F4/Ctrl+W/F2/Delete used to be hardcoded,
    // not editable here at all) — a real KeyboardSettings instance (not a further mock) so the
    // round-trip through SetPref is genuinely observable on the same object MainWindow/FileTreeView
    // read from.
    [Fact]
    public void HotkeyProperties_Should_RoundTrip_ThroughTheUnderlyingKeyboardSettings()
    {
        var keyboard = new KeyboardSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.HotkeyOpenDependencies = "Ctrl+Alt+D";
        vm.HotkeyCloseTab = "Ctrl+Shift+W";
        vm.HotkeyRenameFile = "Ctrl+R";
        vm.HotkeyDeleteFile = "Ctrl+Delete";

        Assert.Equal("Ctrl+Alt+D", keyboard.OpenDependencies);
        Assert.Equal("Ctrl+Shift+W", keyboard.CloseTab);
        Assert.Equal("Ctrl+R", keyboard.RenameFile);
        Assert.Equal("Ctrl+Delete", keyboard.DeleteFile);
        prefs.Verify(p => p.Save(), Times.Exactly(4));
    }

    // Newly wired global switch — same round-trip pattern as the hotkey properties
    // above.
    [Fact]
    public void CommitCharactersEnabled_Should_RoundTrip_ThroughTheUnderlyingEditorSettings()
    {
        var editorSettings = new EditorSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(editorSettings);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.CommitCharactersEnabled = false;

        Assert.False(editorSettings.CommitCharactersEnabled);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    // Live user request: Preferences > Editor exposes the language+kind Ctrl+N/File >
    // New File use to skip NewFileKindDialog, and Preferences > Keyboard exposes the new hotkey
    // itself — same round-trip pattern as the other newly-wired settings above.
    [Fact]
    public void NewFileDefaults_Should_RoundTrip_ThroughTheUnderlyingEditorSettings()
    {
        var editorSettings = new EditorSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(editorSettings);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.NewFileLanguage = RpadLanguage.Vb;
        vm.NewFileKind = RpadKind.Program;

        Assert.Equal(RpadLanguage.Vb, editorSettings.NewFileLanguage);
        Assert.Equal(RpadKind.Program, editorSettings.NewFileKind);
        prefs.Verify(p => p.Save(), Times.Exactly(2));
    }

    [Fact]
    public void HotkeyNewFile_Should_RoundTrip_ThroughTheUnderlyingKeyboardSettings()
    {
        var keyboard = new KeyboardSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.HotkeyNewFile = "Ctrl+Alt+N";

        Assert.Equal("Ctrl+Alt+N", keyboard.NewFile);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    // Every registered language today is .NET-based, so they
    // all nest one level deeper, under a ".NET" runtime-family group — Languages -> .NET -> C#/VB —
    // rather than sitting directly under Languages the way they used to.
    [Fact]
    public void Constructor_Should_BuildOneLanguageChildNode_PerRegisteredLanguage()
    {
        var vm = CreateViewModel(CreateRealRegistry());

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet" && n.Name == ".NET");
        Assert.Equal(2, dotnetNode.Children.Count);
        Assert.Contains(dotnetNode.Children, c => c.Id == "languages.csharp" && c.Name == "C#");
        Assert.Contains(dotnetNode.Children, c => c.Id == "languages.vb" && c.Name == "Visual Basic");
    }

    // A third registered language needs no new property/XAML panel — it just has to show up as
    // another child node, which is the whole point of deriving the tree from the registry. (It
    // lands under the same ".NET" group as C#/VB today — see the constructor's own comment on why a
    // real per-language RuntimeFamily isn't built until there's an actual non-.NET language to need
    // it — so this fake, not itself .NET-based, is still asserted as a ".NET" child; that's the
    // known, documented interim state, not what this test is checking.)
    [Fact]
    public void Constructor_Should_IncludeAThirdLanguage_WithNoCodeChangeBeyondRegistering()
    {
        var registry = CreateRealRegistry();
        registry.Register(new FakeThirdLanguageFactory());

        var vm = CreateViewModel(registry);

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet");
        Assert.Equal(3, dotnetNode.Children.Count);
        Assert.Contains(dotnetNode.Children, c => c.Id == "languages.fake" && c.Name == "Fake Lang");
    }

    [Fact]
    public void IsLanguageNodeSelected_Should_BeFalse_WhenTheParentLanguagesNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");

        vm.SelectedNode = languagesNode;

        Assert.True(vm.IsLanguagesSelected);
        Assert.False(vm.IsLanguageNodeSelected);
        Assert.Null(vm.SelectedLanguageSettingsPlaceholder);
    }

    // The ".NET" group node itself is not a language node (IsLanguageNodeSelected) — it has its own
    // real settings panel (SDK/debugger paths, IsDotnetSelected), not the generic "no settings yet"
    // placeholder every actual language shares.
    [Fact]
    public void IsDotnetSelected_Should_BeTrue_AndIsLanguageNodeSelected_BeFalse_WhenTheDotnetGroupNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet");

        vm.SelectedNode = dotnetNode;

        Assert.True(vm.IsDotnetSelected);
        Assert.False(vm.IsLanguageNodeSelected);
        Assert.Null(vm.SelectedLanguageSettingsPlaceholder);
    }

    // C# is now the first language with a real
    // settings panel of its own, instead of the shared "no settings yet" placeholder every other
    // language still gets (see the Visual Basic test right below this one, unchanged).
    // IsLanguageNodeSelected itself stays true either way (still one of Languages' own child nodes) —
    // only the placeholder-vs-real-panel split changes.
    [Fact]
    public void IsCSharpNodeSelected_Should_BeTrue_WithNoGenericPlaceholder_WhenTheCSharpChildNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet");
        var csharpNode = Assert.Single(dotnetNode.Children, c => c.Id == "languages.csharp");

        vm.SelectedNode = csharpNode;

        Assert.False(vm.IsLanguagesSelected);
        Assert.False(vm.IsDotnetSelected);
        Assert.True(vm.IsLanguageNodeSelected);
        Assert.True(vm.IsCSharpNodeSelected);
        Assert.False(vm.IsGenericLanguagePlaceholderVisible);
        Assert.Null(vm.SelectedLanguageSettingsPlaceholder);
    }

    [Fact]
    public void InlayHintsEnabled_Should_RoundTrip_ThroughTheUnderlyingCSharpSettings()
    {
        var csharpSettings = new CSharpLanguageSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.CSharp).Returns(csharpSettings);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.InlayHintsEnabled = false;

        Assert.False(csharpSettings.InlayHintsEnabled);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public void CodeFixesEnabled_Should_RoundTrip_ThroughTheUnderlyingCSharpSettings()
    {
        var csharpSettings = new CSharpLanguageSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.CSharp).Returns(csharpSettings);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.CodeFixesEnabled = false;

        Assert.False(csharpSettings.CodeFixesEnabled);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public void ExcludedDiagnosticIdsText_Should_RoundTrip_AsOneIdPerLine_TrimmingBlankLines()
    {
        var csharpSettings = new CSharpLanguageSettings();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.CSharp).Returns(csharpSettings);
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, CreateRealRegistry());

        vm.ExcludedDiagnosticIdsText = "CS0184\n\n  IDE0007  \n";

        Assert.Equal(["CS0184", "IDE0007"], csharpSettings.ExcludedDiagnosticIds);
        Assert.Equal("CS0184\nIDE0007", vm.ExcludedDiagnosticIdsText);
    }

    // User request 2026-07-21 (pass 106): "fix formatting" (IDE0055) should be excluded
    // out of the box — RunPad already has its own dotnet-format-based formatting story, so a live
    // "reformat this" suggestion on every whitespace deviation was judged more noise than help by
    // default. A fresh CSharpLanguageSettings (e.g. a first-ever launch, before settings.json exists)
    // should reflect that without the user having to discover and type the ID themselves.
    [Fact]
    public void CSharpLanguageSettings_Should_ExcludeFixFormatting_ByDefault()
        => Assert.Contains("IDE0055", new CSharpLanguageSettings().ExcludedDiagnosticIds);

    [Fact]
    public void IsLanguageNodeSelected_Should_BeTrue_WhenAVisualBasicChildNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet");
        var vbNode = Assert.Single(dotnetNode.Children, c => c.Id == "languages.vb");

        vm.SelectedNode = vbNode;

        Assert.True(vm.IsLanguageNodeSelected);
        Assert.Equal("No Visual Basic-specific settings yet.", vm.SelectedLanguageSettingsPlaceholder);
    }

    [Fact]
    public void FilterText_MatchingALanguageName_Should_StillSurfaceTheParentLanguagesAndDotnetNodes()
    {
        var vm = CreateViewModel(CreateRealRegistry());

        vm.FilterText = "c#";

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var dotnetNode = Assert.Single(languagesNode.Children, n => n.Id == "dotnet");
        Assert.Single(dotnetNode.Children, c => c.Id == "languages.csharp");
        Assert.DoesNotContain(dotnetNode.Children, c => c.Id == "languages.vb");
    }

    private sealed class FakeThirdLanguageFactory : ILanguageHostFactory
    {
        public string Language => "fake";
        public string DisplayName => "Fake Lang";
        public LanguageHostCapabilities Capabilities => LanguageHostCapabilities.Run;
        public IReadOnlyDictionary<string, string> Snippets => new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile => new();
        public ILanguageHost Create(ILanguageHostOptions? options = null) =>
            throw new NotSupportedException();
    }
}
