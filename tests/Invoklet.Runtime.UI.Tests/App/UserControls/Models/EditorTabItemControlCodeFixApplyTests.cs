using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.CodeTextEditor.CodeFix;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// End-to-end coverage of the
// deferred-materialization click flow: a REAL CSharpIntelliSenseService (not mocked — the point is
// whether the whole RoslynCodeFixEngine cache + EditorTabItemControl.ApplyCodeFixAsync pipeline
// actually applies the right result when a specific lightbulb entry is clicked, not whether some mock
// was configured correctly) driving a real EditorsTabItemControl, clicking through the actual
// CodeFixMargin menu-item wiring (same idiom as CodeFixMarginTests.cs's own click test) rather than
// simulating the click by calling internal methods directly. The "add using" fix this test drives
// lands in FrontMatter.Namespaces, not the visible editor text — every Invoklet tab (even a brand-new
// untitled one, see NewMdRunFile) is a real .inv document, and usings are project-level metadata
// there (docs/ImplementationPlanRPad.md), not text inserted into the script itself.
[Trait("Category", "Integration")]
[Trait("Category", "Roslyn")]
public class EditorTabItemControlCodeFixApplyTests
{
    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static Mock<IThemeService> MakeTheme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private static CSharpIntelliSenseService CreateRealIntelliSense()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [AvaloniaFact]
    public async Task ClickingALightbulbEntry_Should_MaterializeAndApplyItsFix_ToTheDocument()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();
        using var intelliSense = CreateRealIntelliSense();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense, buildCache.Object, timings: NoPauses.Timings);
        var host = new Window { Content = ctrl };
        host.Show();

        var editor = ctrl.State.Editor;
        var margin = editor.TextArea.LeftMargins.OfType<CodeFixMargin>().Single();

        // Regex isn't in the implicit-usings preamble — CS0103 fixable by adding
        // `using System.Text.RegularExpressions;` (real CSharpAddImportCodeFixProvider fix).
        const string code = "var m = Regex.Match(\"a\", \"a\");";
        // editor.Text's setter already fires TextChanged -> NotifyTextChanged(editor.Text)
        // (SetupEditor's own wiring) — no need to also call NotifyTextChanged here, and doing so
        // would start a SECOND, independent debounced analysis pass that could replace
        // RoslynCodeFixEngine's cache (wholesale, by design) right as this test is mid-poll, handing
        // back a CodeFixEntry whose Key belongs to whichever pass finished first.
        editor.Text = code;

        // Multiple real fixes apply here (e.g. CSharpFullyQualifyCodeFixProvider's "Fully qualify
        // 'Regex'" ALSO mentions Regex in its title) — matching specifically on "using" picks out
        // CSharpAddImportCodeFixProvider's own fix, not just any Regex-related one.
        CodeFixEntry? FindUsingFix() =>
            margin.Fixes.FirstOrDefault(e => e.Title.Contains("using", StringComparison.OrdinalIgnoreCase));
        await TestWait.WhileAsync(() => FindUsingFix() is null);
        var entry = FindUsingFix();
        Assert.NotNull(entry);

        var item = margin.BuildMenuItem(entry);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        // A new untitled tab still gets a real MdRunDocument (every Invoklet script is a .inv document
        // — see NewMdRunFile), so this "add using" fix's namespace goes to FrontMatter.Namespaces
        // (docs/ImplementationPlanRPad.md — usings are project-level metadata), NOT into the visible
        // editor text (which is why the raw text is never expected to change here at all).
        // FrontMatter.Namespaces is already a real (if empty) list from tab creation onward — poll
        // for the NAMESPACE showing up, not merely for the list itself becoming non-null.
        await TestWait.WhileAsync(() =>
            ctrl.State.MdRunDocument?.FrontMatter.Namespaces?.Contains("System.Text.RegularExpressions") != true);
        var namespaces = ctrl.State.MdRunDocument?.FrontMatter.Namespaces;

        Assert.NotNull(namespaces);
        Assert.Contains("System.Text.RegularExpressions", namespaces);
        Assert.Equal(code, editor.Text); // visible script body itself is untouched
    }
}
