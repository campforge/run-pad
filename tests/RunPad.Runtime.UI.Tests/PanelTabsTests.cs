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
    public void BothTabRows_Should_LookLikeDocumentTabs(string theme)
    {
        var outputTabs = OutputTabs(out var outputHost, theme);
        var debuggerTabs = DebuggerTabs(out var debuggerHost, theme);
        foreach (var tabs in new[] { outputTabs, debuggerTabs })
        {
            Assert.Contains("panel", tabs.Classes);
            var items = Items(tabs);
            var selected = items[tabs.SelectedIndex];
            var content = Resource(tabs, "RunPad.Output.BackgroundBrush");
            var strip = Resource(tabs, "RunPad.Panel.BackgroundBrush");

            Assert.Equal(content, ColorOf(Root(selected).Background));
            Assert.Equal(content, ColorOf(tabs.Background));
            Assert.All(items.Where(i => i != selected), i => Assert.Equal(strip, ColorOf(Root(i).Background)));
            Assert.Equal(strip, ColorOf(tabs.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Strip").Background));
            Assert.All(items, i => Assert.Equal(new Thickness(0, 0, 1, 0), Root(i).BorderThickness));
            // No accent line anywhere in the row while nothing has keyboard focus.
            var accent = Resource(tabs, "RunPad.Tab.AccentBrush");
            Assert.DoesNotContain(items.SelectMany(i => i.GetVisualDescendants().OfType<Border>()),
                b => b.Background is ISolidColorBrush bg && bg.Color == accent || b.BorderBrush is ISolidColorBrush bb && bb.Color == accent);
            Assert.Equal(Resource(tabs, "RunPad.Text.PrimaryBrush"), ColorOf(selected.Foreground));
            Assert.All(items.Where(i => i != selected), i => Assert.Equal(Resource(tabs, "RunPad.Text.SecondaryBrush"), ColorOf(i.Foreground)));
        }
        outputHost.Close();
        debuggerHost.Close();
    }

    // The Debugger's own row stays the smaller one inside the panel row.
    [AvaloniaFact]
    public void TheNestedRow_Should_BeSmallerThanThePanelRow()
    {
        var outer = Items(OutputTabs(out var outputHost));
        var inner = Items(DebuggerTabs(out var debuggerHost));

        Assert.All(outer, i => Assert.Equal((28d, 13d), (i.Bounds.Height, i.FontSize)));
        Assert.All(inner, i => Assert.Equal((24d, 12d), (i.Bounds.Height, i.FontSize)));
        outputHost.Close();
        debuggerHost.Close();
    }

    [AvaloniaFact]
    public void Clicking_AndTheArrowKeys_Should_SwitchTabs_WithAFrameOnlyForKeyboardFocus()
    {
        var tabs = DebuggerTabs(out var host);
        var items = Items(tabs);
        Border Frame(TabItem i) => i.GetVisualDescendants().OfType<Border>().First(b => b.Name == "FocusFrame");
        var accent = Resource(tabs, "RunPad.Tab.AccentBrush");

        // A click anywhere on the tab, its padding included, selects it — no focus frame.
        var watch = items[2];
        host.MouseDown(watch.TranslatePoint(new Point(2, 2), host)!.Value, MouseButton.Left);
        host.MouseUp(watch.TranslatePoint(new Point(2, 2), host)!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, tabs.SelectedIndex);
        Assert.NotEqual(accent, ColorOf(Frame(watch).BorderBrush));

        // Keyboard: Right/Left move the selection; the tab reached by the keyboard shows the frame.
        host.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, tabs.SelectedIndex);
        Assert.Equal(accent, ColorOf(Frame(items[3]).BorderBrush));
        Assert.NotEqual(accent, ColorOf(Frame(watch).BorderBrush));
        host.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, "");
        host.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, "");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, tabs.SelectedIndex);
        host.Close();
    }
}
