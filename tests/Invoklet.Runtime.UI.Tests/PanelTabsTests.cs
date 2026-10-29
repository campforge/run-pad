using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Compact tool selectors, simultaneous stack/variables and theme-aware status presentation.
public class PanelTabsTests
{
    private static Window Host(Control content, string theme = "VsCodeDark")
    {
        var window = new Window { Width = 900, Height = 600, Content = content };
        window.Styles.Add(new InvokletTheme());
        window.RequestedThemeVariant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(theme)!.GetValue(null)!;
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
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
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

    [AvaloniaFact]
    public async Task CompiledIl_Should_TransferTheWorkerDocumentToTheUiThread()
    {
        var tabs = OutputTabs(out var host);
        try
        {
            tabs.SelectedIndex = 2;
            var tab = Assert.IsType<EditorsTabItemControl>(host.Content);
            var update = typeof(EditorsTabItemControl).GetMethod("UpdateIlOutput",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await Task.Run(() => update.Invoke(tab, [typeof(RpadDocument).Assembly.Location, 0]), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var editor = host.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "IlOutput");
            Assert.Contains(".class", editor.Text, StringComparison.Ordinal);
            Assert.Contains("RpadDocument", editor.Text, StringComparison.Ordinal);
            Assert.True(editor.Document.LineCount > 1);
            var current = editor.Document;
            await Task.Run(() => update.Invoke(tab, [typeof(RpadDocument).Assembly.Location, -1]), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(current, editor.Document);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void LargeIlOutput_Should_UseVirtualizedVisibleLines_AndRemainReadOnly()
    {
        var tabs = OutputTabs(out var host);
        try
        {
            tabs.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var editor = host.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "IlOutput");
            editor.Text = string.Concat(Enumerable.Repeat("IL_0000: nop\n", 100_000));
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.WordWrap);
            Assert.Equal(100_001, editor.Document.LineCount);
            Assert.InRange(editor.TextArea.TextView.VisualLines.Count, 1, 200);
            editor.Select(0, 7);
            Assert.Equal("IL_0000", editor.SelectedText);
        }
        finally { host.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    [InlineData("RiderDark")]
    [InlineData("InvokletWarmLight")]
    public void ToolSelectors_Should_UseSoftSelectionAndThemeColours(string theme)
    {
        var outputTabs = OutputTabs(out var outputHost, theme);
        var debuggerTabs = DebuggerTabs(out var debuggerHost, theme);
        foreach (var tabs in new[] { outputTabs, debuggerTabs })
        {
            Assert.Contains("tool", tabs.Classes);
            var items = Items(tabs);
            var selected = items[tabs.SelectedIndex];
            Assert.Equal(Resource(tabs, "Invoklet.ToolTab.SelectedBackgroundBrush"), ColorOf(Root(selected).Background));
            Assert.Equal(Resource(tabs, "Invoklet.ToolTab.SelectedForegroundBrush"), ColorOf(selected.Foreground));
            Assert.All(items, i => Assert.Equal(new CornerRadius(4), Root(i).CornerRadius));
        }
        outputHost.Close();
        debuggerHost.Close();
    }

    [AvaloniaTheory]
    [InlineData(260)]
    [InlineData(500)]
    [InlineData(900)]
    public void StackAndVariables_Should_BeVisibleTogether_WhileSwitchingViews(int width)
    {
        var output = OutputTabs(out var host);
        host.Width = width;
        var exceptions = Assert.IsAssignableFrom<Button>(output.Tag);
        Assert.True(exceptions.IsVisible);
        output.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(exceptions.IsVisible);
        var panel = output.GetVisualDescendants().OfType<DebugPanel>().Single();
        var vm = Assert.IsType<DebugPanelViewModel>(panel.DataContext);
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
        var headers = Items(tabs).Select(t => t.Header).OfType<string>().ToArray();
        Assert.Contains("Locals", headers);
        Assert.Contains("Watch", headers);
        var trailing = exceptions.TranslatePoint(new Point(exceptions.Bounds.Width, 0), output)!.Value;
        Assert.InRange(trailing.X, output.Bounds.Width - 9, output.Bounds.Width - 7);
        Assert.IsType<Invoklet.TemplatedControls.IconButton>(exceptions);
        var threadGrid = panel.FindControl<DataGrid>("ThreadsGrid")!;
        Assert.True(threadGrid.IsEffectivelyVisible);
        Assert.Same(vm.DebugThreads, threadGrid.ItemsSource);
        Assert.True(threadGrid.IsReadOnly);
        var threadBottom = threadGrid.TranslatePoint(new Point(0, threadGrid.Bounds.Height), host)!.Value.Y;
        Assert.True(threadBottom < stack.TranslatePoint(default, host)!.Value.Y);
        Assert.DoesNotContain(panel.GetVisualDescendants(), v => v is Expander);
        var splitter = panel.FindControl<GridSplitter>("ThreadsStackSplitter")!;
        Assert.True(splitter.IsVisible);
        Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
        var headings = panel.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is "Threads" or "Call Stack").ToArray();
        Assert.Equal(2, headings.Length);
        Assert.All(headings, h => Assert.Equal(new Thickness(6, 2), h.Padding));
        exceptions.Flyout!.ShowAt(exceptions);
        Dispatcher.UIThread.RunJobs();
        var flyout = Assert.IsType<Flyout>(exceptions.Flyout);
        var checks = ((Control)flyout.Content!).GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(new[]
        {
            "Wrap long lines in results",
            "Break on unhandled exceptions",
            "Break on all exceptions (including caught)"
        }, checks.Select(c => c.Content));
        var wrap = checks[0];
        var breakUnhandled = checks[1];
        var breakAll = Assert.Single(checks, c => Equals(c.Content, "Break on all exceptions (including caught)"));
        Assert.True(wrap.IsChecked);
        Assert.True(breakUnhandled.IsChecked);
        Assert.False(breakAll.IsChecked);
        breakUnhandled.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.BreakOnUncaughtExceptions);
        breakAll.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.BreakOnAllExceptions);
        flyout.Hide();
        output.SelectedIndex = 0;
        Assert.True(exceptions.IsVisible);
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
    [InlineData("InvokletMidnight")]
    [InlineData("InvokletWarmLight")]
    public void StatusStrip_Should_KeepTextReadable_AndIndicateStateWithoutColouringText(string theme)
    {
        var tabs = OutputTabs(out var host, theme);
        var control = tabs.GetVisualAncestors().OfType<EditorsTabItemControl>().First();
        var text = control.FindControl<TextBlock>("ExecStatusBlock")!;
        var indicator = control.FindControl<global::Avalonia.Controls.Shapes.Ellipse>("ExecStatusIndicator")!;
        var background = Resource(text, "Invoklet.StatusBar.BackgroundBrush");
        Assert.Equal(Resource(text, "Invoklet.Panel.BackgroundBrush"), background);
        Assert.True(Contrast(ColorOf(text.Foreground), background) >= 4.5);
        Assert.Equal(Resource(text, "Invoklet.Text.SecondaryBrush"), ColorOf(text.Foreground));
        foreach (var state in new[] { "Running", "Cancelled", "Success", "Error", "Paused" })
            Assert.True(Contrast(Resource(text, $"Invoklet.Status.{state}Brush"), background) >= 3, theme + "/" + state);
        Assert.Equal("Ready", text.Text);
        Assert.Equal(Resource(text, "Invoklet.Text.SecondaryBrush"), ColorOf(indicator.Fill));
        control.ResumeDebug(Invoklet.Contracts.Models.DebugAction.Continue);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Process", text.Text);
        Assert.Equal(Resource(text, "Invoklet.Status.RunningBrush"), ColorOf(indicator.Fill));
        host.RequestedThemeVariant = InvokletThemeVariants.RiderLight;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Resource(text, "Invoklet.Status.RunningBrush"), ColorOf(indicator.Fill));
        Assert.Equal(Resource(text, "Invoklet.StatusBar.ForegroundBrush"), ColorOf(text.Foreground));
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
