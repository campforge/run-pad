using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Audit finding: the constructor subscribed to _vm.DebugPanel.WatchExpressions.
// CollectionChanged with an anonymous lambda, so Dispose() (which carefully unsubscribes
// _prefs.Changed/_selectedKindSub) had no handler reference to unsubscribe it with. Not an active
// leak today (control and VM are always created/destroyed as a pair — EditorTabsControl.cs), but a
// real one waiting for the first change to that lifecycle assumption. Fixed by naming the handler.
public class EditorTabItemControlWatchExpressionsDisposalTests
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

    [AvaloniaFact]
    public void Dispose_Should_UnsubscribeFromWatchExpressionsCollectionChanged()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            filePath: null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new CSharpReferenceService(), buildCache.Object);
        var host = new Window { Content = ctrl };
        host.Show();

        ctrl.Dispose();

        // Non-empty Expression, set before adding — if the (fixed) CollectionChanged handler still
        // fired, EvaluateWatchAsync would take the "not paused" branch and overwrite Value; the
        // freshly-constructed default ("?") only survives if the handler truly stopped firing.
        var w = new WatchExpression { Expression = "1+1" };
        vm.DebugPanel.WatchExpressions.Add(w);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("?", w.Value);
    }
}
