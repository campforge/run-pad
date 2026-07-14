using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Languages.CSharp;
using DotPad.Languages.VB;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO.md #49 item 4: the Languages settings tree used to be two hardcoded SettingsNodes
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

    [Fact]
    public void Constructor_Should_BuildOneLanguageChildNode_PerRegisteredLanguage()
    {
        var vm = CreateViewModel(CreateRealRegistry());

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        Assert.Equal(2, languagesNode.Children.Count);
        Assert.Contains(languagesNode.Children, c => c.Id == "languages.csharp" && c.Name == "C#");
        Assert.Contains(languagesNode.Children, c => c.Id == "languages.vb" && c.Name == "Visual Basic");
    }

    // A third registered language needs no new property/XAML panel — it just has to show up as
    // another child node, which is the whole point of deriving the tree from the registry.
    [Fact]
    public void Constructor_Should_IncludeAThirdLanguage_WithNoCodeChangeBeyondRegistering()
    {
        var registry = CreateRealRegistry();
        registry.Register(new FakeThirdLanguageFactory());

        var vm = CreateViewModel(registry);

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        Assert.Equal(3, languagesNode.Children.Count);
        Assert.Contains(languagesNode.Children, c => c.Id == "languages.fake" && c.Name == "Fake Lang");
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

    [Fact]
    public void IsLanguageNodeSelected_Should_BeTrue_WhenACSharpChildNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var csharpNode = Assert.Single(languagesNode.Children, c => c.Id == "languages.csharp");

        vm.SelectedNode = csharpNode;

        Assert.False(vm.IsLanguagesSelected);
        Assert.True(vm.IsLanguageNodeSelected);
        Assert.Equal("No C#-specific settings yet.", vm.SelectedLanguageSettingsPlaceholder);
    }

    [Fact]
    public void IsLanguageNodeSelected_Should_BeTrue_WhenAVisualBasicChildNodeIsSelected()
    {
        var vm = CreateViewModel(CreateRealRegistry());
        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        var vbNode = Assert.Single(languagesNode.Children, c => c.Id == "languages.vb");

        vm.SelectedNode = vbNode;

        Assert.True(vm.IsLanguageNodeSelected);
        Assert.Equal("No Visual Basic-specific settings yet.", vm.SelectedLanguageSettingsPlaceholder);
    }

    [Fact]
    public void FilterText_MatchingALanguageName_Should_StillSurfaceTheParentLanguagesNode()
    {
        var vm = CreateViewModel(CreateRealRegistry());

        vm.FilterText = "c#";

        var languagesNode = Assert.Single(vm.Nodes, n => n.Id == "languages");
        Assert.Single(languagesNode.Children, c => c.Id == "languages.csharp");
        Assert.DoesNotContain(languagesNode.Children, c => c.Id == "languages.vb");
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
