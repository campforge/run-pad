using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.UserControls.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;


public class EditorTabItemControlToolbarHeightTests
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
    public void ToolbarBorder_Should_RenderAt38PixelsTall_NotTheOlder46()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(filePath: null, [], prefs.Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new CSharpReferenceService(), buildCache.Object, editorBackground: null);
        var host = new Window { Content = ctrl, Width = 800, Height = 600 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var toolbar = ctrl.FindControl<Border>("ToolbarBorder")!;

        // Row is 38px tall, Border has Margin="6,4" (4px top + 4px bottom) — 38 - 8 = 30.
        Assert.Equal(30, toolbar.Bounds.Height);
    }
}
