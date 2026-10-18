using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;
using DiagnosticSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.Runtime.UI.Tests;

// Selecting another SDK changes what the editor checks the script against (references, C# version,
// conditional symbols). The squiggles and lightbulb must follow straight away, not only after the
// next keystroke, also when the text itself never changes.
public class EditorTabSdkChangeAnalysisTests
{
    private static readonly DotnetSdk Current = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    // .NET 5 compiles with C# 9 by default; no .NET 5 targeting pack is needed for that, so the
    // test does not depend on which packs the machine has.
    private static readonly DotnetSdk Older = new("5.0.100", "net5.0", "5.0.0", ".NET 5.0");

    // A record struct needs C# 10.
    private const string Code = "var p = new P(1);\nConsole.WriteLine(p.X);\nrecord struct P(int X);\n";

    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        return prefs;
    }

    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm) CreateControl(IIntelliSenseService intelliSense)
    {
        var prefs = MakePrefs();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new EditorTabItemViewModel(
            null, [Current, Older], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().Object,
            prefs.Object, intelliSense, new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return (ctrl, vm);
    }

    private static void Close(EditorsTabItemControl ctrl)
    {
        ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
        ctrl.Dispose();
    }

    // The trigger itself: after the new target's context reaches IntelliSense, the unchanged text is
    // analysed again — away and back.
    [AvaloniaFact]
    public void SelectingAnotherSdk_Should_ReanalyseTheUnchangedText_AfterApplyingItsContext()
    {
        var calls = new List<string>();
        var intelliSense = new Mock<IIntelliSenseService>().ForwardingExecutionContext();
        intelliSense.Setup(s => s.UpdatePackageReferences(It.IsAny<IReadOnlyDictionary<string, Package>>(), It.IsAny<PackageReferenceContext>()))
            .Callback<IReadOnlyDictionary<string, Package>, PackageReferenceContext>((_, c) => calls.Add("context " + c.TargetFramework));
        intelliSense.Setup(s => s.NotifyTextChanged(It.IsAny<string>())).Callback<string>(t => calls.Add("analyse " + t));
        var (ctrl, vm) = CreateControl(intelliSense.Object);
        try
        {
            ctrl.State.Editor.Text = Code;

            calls.Clear();
            vm.SelectedSdk = Older;
            Assert.Equal(["context net5.0", "analyse " + Code], calls);

            calls.Clear();
            vm.SelectedSdk = Current;
            Assert.Equal(["context net10.0", "analyse " + Code], calls);
            Assert.Equal(Code, ctrl.State.Editor.Text);
        }
        finally
        {
            Close(ctrl);
        }
    }

    // What the user sees, with the real C# IntelliSense: the C# 10 construct becomes an error for
    // .NET 5 (C# 9) and stops being one for .NET 10, without typing.
    [AvaloniaFact]
    public async Task SelectingAnotherSdk_Should_UpdateTheDiagnostics_WithoutAnEdit()
    {
        var referenceService = new CSharpReferenceService();
        // Disposed with the tab.
        var intelliSense = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
        IReadOnlyList<IDiagnosticInfo>? latest = null;
        var passes = 0;
        intelliSense.DiagnosticsChanged += (code, diagnostics) =>
        {
            if (code != Code) return;
            Volatile.Write(ref latest, diagnostics);
            Interlocked.Increment(ref passes);
        };
        static bool HasFeatureError(IReadOnlyList<IDiagnosticInfo>? d) =>
            d?.Any(x => x.Severity == DiagnosticSeverity.Error && x.Message.Contains("record structs", StringComparison.Ordinal)
                && x.Message.Contains("C# 9.0", StringComparison.Ordinal)) == true;
        static bool HasErrors(IReadOnlyList<IDiagnosticInfo>? d) => d?.Any(x => x.Severity == DiagnosticSeverity.Error) == true;

        var (ctrl, vm) = CreateControl(intelliSense);
        try
        {
            ctrl.State.Editor.Text = Code;
            await TestWait.WhileAsync(() => Volatile.Read(ref passes) == 0);
            Assert.False(HasErrors(Volatile.Read(ref latest)));

            var before = Volatile.Read(ref passes);
            vm.SelectedSdk = Older;
            await TestWait.WhileAsync(() => !HasFeatureError(Volatile.Read(ref latest)));
            Assert.True(Volatile.Read(ref passes) > before, "no analysis pass after selecting the older SDK");
            Assert.True(HasFeatureError(Volatile.Read(ref latest)));

            vm.SelectedSdk = Current;
            await TestWait.WhileAsync(() => HasErrors(Volatile.Read(ref latest)));
            Assert.False(HasErrors(Volatile.Read(ref latest)));
            Assert.Equal(Code, ctrl.State.Editor.Text);
        }
        finally
        {
            Close(ctrl);
        }
    }
}
