using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

// Results/Debugger/IL and the Debugger's Threads/Locals/… tabs share the document tabs' look: inactive
// tabs on the panel colour, the selected one in its content's colour (so they join), no accent line,
// a frame only for keyboard focus; the Debugger's row is the smaller, nested one. Clicking and the
// arrow keys still switch tabs. Real controls with the real RunPadTheme.
public class PanelTabsTests
{
    private static Window Host(Control content, string theme = "VsCodeDark")
    {
        var window = new Window { Width = 900, Height = 600, Content = content };
        window.Styles.Add(new RunPadTheme());
        window.RequestedThemeVariant = (ThemeVariant)typeof(RunPadThemeVariants).GetField(theme)!.GetValue(null)!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static TabControl OutputTabs(out Window host, string theme = "VsCodeDark")
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());
        var themeService = new Mock<IThemeService>();
        themeService.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new EditorTabItemViewModel(null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var tab = new EditorsTabItemControl(vm, themeService.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense: null, new CSharpReferenceService(),
            new Mock<IBuildCacheService>().Object);
        host = Host(tab, theme);
        return tab.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "OutputTabsControl");
    }

    private static TabControl DebuggerTabs(out Window host, string theme = "VsCodeDark")
    {
        var panel = new DebugPanel { DataContext = new DebugPanelViewModel() };
        host = Host(panel, theme);
        return panel.GetVisualDescendants().OfType<TabControl>().Single();
    }

    private static List<TabItem> Items(TabControl tabs) => tabs.GetLogicalChildren().OfType<TabItem>().ToList();

    private static Border Root(TabItem item) => item.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Root");

    private static Color ColorOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    private static Color Resource(Control c, string key)
    {
        Assert.True(c.TryFindResource(key, c.ActualThemeVariant, out var v), key);
        return ((ISolidColorBrush)v!).Color;
    }

    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    [InlineData("RiderDark")]
    [InlineData("RunPadWarmLight")]
    public void ToolSelectors_Should_UseSoftSelectionAndThemeColours(string theme)
    {
        var outputTabs = OutputTabs(out var outputHost, theme);
        var debuggerTabs = DebuggerTabs(out var debuggerHost, theme);
        foreach (var tabs in new[] { outputTabs, debuggerTabs })
        {
            Assert.Contains("tool", tabs.Classes);
            var items = Items(tabs);
            var selected = items[tabs.SelectedIndex];
            Assert.Equal(Resource(tabs, "RunPad.Accent.BackgroundBrush"), ColorOf(Root(selected).Background));
            Assert.Equal(Resource(tabs, "RunPad.Accent.ForegroundBrush"), ColorOf(selected.Foreground));
            Assert.All(items, i => Assert.Equal(new CornerRadius(4), Root(i).CornerRadius));
        }
        outputHost.Close();
        debuggerHost.Close();
    }

    [AvaloniaTheory]
    [InlineData(500)]
    [InlineData(900)]
    public void StackAndVariables_Should_BeVisibleTogether_WhileSwitchingViews(int width)
    {
        var vm = new DebugPanelViewModel();
        var panel = new DebugPanel { DataContext = vm };
        var host = Host(panel);
        host.Width = width;
        Dispatcher.UIThread.RunJobs();
        var stack = panel.FindControl<DataGrid>("CallStackGrid")!;
        var tabs = panel.FindControl<TabControl>("VariableTabs")!;
        var stackOrigin = stack.TranslatePoint(default, host)!.Value;
        var tabsOrigin = tabs.TranslatePoint(default, host)!.Value;
        Assert.True(stackOrigin.X + stack.Bounds.Width <= tabsOrigin.X);
        foreach (var index in new[] { 0, 1, 0 })
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
            Assert.True(stack.IsEffectivelyVisible);
            Assert.True(stack.Bounds.Width > 0);
            Assert.True(tabs.Bounds.Width > 0);
        }
        Assert.Equal(new[] { "Locals", "Watch" }, Items(tabs).Select(t => t.Header));
        var exceptions = tabs.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExceptionsButton");
        exceptions.Flyout!.ShowAt(exceptions);
        Dispatcher.UIThread.RunJobs();
        var flyout = Assert.IsType<Flyout>(exceptions.Flyout);
        var checks = ((Control)flyout.Content!).GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(2, checks.Length);
        Assert.True(checks[0].IsChecked);
        checks[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.BreakOnAllExceptions);
        flyout.Hide();
        host.Close();
    }

    [AvaloniaFact]
    public void Clicking_AndTheArrowKeys_Should_SwitchTabs_WithAFrameOnlyForKeyboardFocus()
    {
        var tabs = DebuggerTabs(out var host);
        var items = Items(tabs);
        // The keyboard-focus frame is Fluent's focus adorner, as on every other control.
        bool Framed(TabItem i) => FocusFrames.IsShown(i);

        // A click anywhere on the tab, its padding included, selects it — no focus frame.
        var watch = items[0];
        host.MouseDown(watch.TranslatePoint(new Point(2, 2), host)!.Value, MouseButton.Left);
        host.MouseUp(watch.TranslatePoint(new Point(2, 2), host)!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.False(Framed(watch));

        // Keyboard: Right/Left move the selection; the tab reached by the keyboard shows the frame.
        host.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(Framed(items[1]));
        Assert.False(Framed(watch));
        host.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, "");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, tabs.SelectedIndex);
        host.Close();
    }
    [AvaloniaTheory]
    [InlineData(260)]
    [InlineData(350)]
    [InlineData(500)]
    public void NarrowPanel_Should_KeepEveryHeaderVisibleAndClickable(int width)
    {
        var tabs = DebuggerTabs(out var host);
        host.Width = width;
        Dispatcher.UIThread.RunJobs();
        var items = Items(tabs);
        foreach (var item in items)
        {
            var origin = item.TranslatePoint(default, host)!.Value;
            Assert.InRange(origin.X, 0, width - item.Bounds.Width);
            Assert.InRange(origin.Y, 0, host.Bounds.Height - item.Bounds.Height);
            var point = item.TranslatePoint(new Point(4, 4), host)!.Value;
            host.MouseDown(point, MouseButton.Left);
            host.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(item, items[tabs.SelectedIndex]);
        }
        host.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, "");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(items.Count - 2, tabs.SelectedIndex);
        host.Close();
    }
    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    [InlineData("RiderDark")]
    [InlineData("RiderLight")]
    [InlineData("Vs2022Dark")]
    [InlineData("Vs2022Light")]
    [InlineData("RunPadMidnight")]
    [InlineData("RunPadWarmLight")]
    public void StatusStrip_Should_KeepTextReadable_AndIndicateStateWithoutColouringText(string theme)
    {
        var tabs = OutputTabs(out var host, theme);
        var control = tabs.GetVisualAncestors().OfType<EditorsTabItemControl>().First();
        var text = control.FindControl<TextBlock>("ExecStatusBlock")!;
        var indicator = control.FindControl<global::Avalonia.Controls.Shapes.Ellipse>("ExecStatusIndicator")!;
        var background = Resource(text, "RunPad.StatusBar.BackgroundBrush");
        Assert.Equal(Resource(text, "RunPad.Panel.BackgroundBrush"), background);
        Assert.True(Contrast(ColorOf(text.Foreground), background) >= 4.5);
        foreach (var state in new[] { "Running", "Cancelled", "Success", "Error", "Paused" })
            Assert.True(Contrast(Resource(text, $"RunPad.Status.{state}Brush"), background) >= 3, theme + "/" + state);
        control.ResumeDebug(RunPad.Contracts.Models.DebugAction.Continue);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Process", text.Text);
        Assert.Equal(Resource(text, "RunPad.Status.RunningBrush"), ColorOf(indicator.Fill));
        host.RequestedThemeVariant = RunPadThemeVariants.RiderLight;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Resource(text, "RunPad.Status.RunningBrush"), ColorOf(indicator.Fill));
        Assert.Equal(Resource(text, "RunPad.StatusBar.ForegroundBrush"), ColorOf(text.Foreground));
        host.Close();
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte v) { var s = v / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
        return (Math.Max(L(a), L(b)) + 0.05) / (Math.Min(L(a), L(b)) + 0.05);
    }
}
