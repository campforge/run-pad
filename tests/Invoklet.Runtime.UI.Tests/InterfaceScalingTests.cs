using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.Services.Scaling;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Code zoom used to belong to each tab (its own FontSize, reset on restart) and nothing scaled the
// rest of the interface. Now two settings drive every window: the interface scale (a layout
// transform around each window's content, code included) and one code zoom shared by every editor.
// Code is shown at FontSize × interface scale × code zoom logical units, the display's pixel
// density coming on top from Avalonia itself.
public class InterfaceScalingTests
{
    private sealed class Prefs
    {
        public Mock<IPreferencesService> Mock { get; } = new();
        public AppSettings Settings { get; }

        public Prefs(AppSettings? settings = null)
        {
            Settings = settings ?? new AppSettings();
            Mock.Setup(p => p.Editor).Returns(Settings.Editor);
            Mock.Setup(p => p.Appearance).Returns(Settings.Appearance);
            Mock.Setup(p => p.CSharp).Returns(Settings.CSharp);
            Mock.Setup(p => p.Keyboard).Returns(Settings.Keyboard);
            Mock.Setup(p => p.Layout).Returns(Settings.Layout);
            Mock.Setup(p => p.Session).Returns(Settings.Session);
            Mock.Setup(p => p.Debugger).Returns(Settings.Debugger);
        }

        public ViewScale Scale => ViewScale.For(Mock.Object);

        // What Preferences → Save does: the file is written, then everyone is told.
        public void SaveFromPreferencesWindow() => Mock.Raise(p => p.Changed += null);
    }

    private static (EditorsTabItemControl Tab, Window Host) OpenTab(Prefs prefs)
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new EditorTabItemViewModel(
            filePath: null, [], prefs.Mock.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var tab = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Mock.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        var host = new Window { Content = tab, Width = 900, Height = 600 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return (tab, host);
    }

    private static string? ZoomButtonText(EditorsTabItemControl tab) => tab.FindControl<Button>("ZoomButton")?.Content as string;

    private static void WheelOverCode(EditorsTabItemControl tab, Window host, double delta)
    {
        var editor = tab.State.Editor;
        var command = editor.CommandModifier == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
        var overText = editor.TextArea.TextView.TranslatePoint(new Point(40, 5), host)!.Value;
        host.MouseWheel(overText, new Vector(0, delta), command);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheWheel_Should_ZoomEveryOpenEditor_AndOneOpenedLater_WritingTheSettingOnce()
    {
        var prefs = new Prefs();
        var (first, firstHost) = OpenTab(prefs);
        var (second, secondHost) = OpenTab(prefs);

        WheelOverCode(first, firstHost, 1);
        WheelOverCode(first, firstHost, 1);

        Assert.Equal(125, prefs.Settings.Editor.ZoomPercent);
        Assert.Equal(12 * 1.25, first.State.Editor.FontSize);
        Assert.Equal(12 * 1.25, second.State.Editor.FontSize);
        Assert.Equal("125%", ZoomButtonText(first));
        Assert.Equal("125%", ZoomButtonText(second));

        var (third, thirdHost) = OpenTab(prefs);
        Assert.Equal(12 * 1.25, third.State.Editor.FontSize);
        Assert.Equal("125%", ZoomButtonText(third));

        // Two wheel ticks, one write — once the burst is over (or on exit).
        prefs.Mock.Verify(p => p.Save(), Times.Never);
        Assert.True(prefs.Scale.HasPendingSave);
        prefs.Scale.Flush();
        prefs.Mock.Verify(p => p.Save(), Times.Once);

        foreach (var host in new[] { firstHost, secondHost, thirdHost }) host.Close();
    }

    [AvaloniaFact]
    public void TheWheel_TheViewMenu_AndTheStatusBarMenu_Should_AllStepTheSameSharedZoom()
    {
        var prefs = new Prefs();
        var (tab, host) = OpenTab(prefs);

        WheelOverCode(tab, host, 1);
        var byWheel = prefs.Settings.Editor.ZoomPercent;
        prefs.Scale.ResetCodeZoom();

        prefs.Scale.ZoomIn(); // what View → Zoom In calls
        var byViewMenu = prefs.Settings.Editor.ZoomPercent;
        prefs.Scale.ResetCodeZoom();

        var statusMenu = (MenuFlyout)tab.FindControl<Button>("ZoomButton")!.Flyout!;
        statusMenu.Items.OfType<MenuItem>().First(i => (string?)i.Header == "Zoom In").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var byStatusMenu = prefs.Settings.Editor.ZoomPercent;

        Assert.Equal(110, byWheel);
        Assert.Equal(110, byViewMenu);
        Assert.Equal(110, byStatusMenu);
        Assert.Equal(12 * 1.1, tab.State.Editor.FontSize, 6);

        WheelOverCode(tab, host, -1);
        Assert.Equal(100, prefs.Settings.Editor.ZoomPercent);
        host.Close();
    }

    // A new font size in Preferences keeps the zoom on top of it; resetting one setting leaves the
    // other, and the font preferences, alone.
    [AvaloniaFact]
    public void FontAndResetChanges_Should_KeepTheOtherSettings()
    {
        var prefs = new Prefs();
        prefs.Settings.Editor.FontFamily = "Menlo";
        prefs.Settings.Editor.WordWrap = true;
        var (tab, host) = OpenTab(prefs);
        prefs.Scale.SetCodeZoomPercent(125);
        prefs.Scale.SetInterfaceScalePercent(150);

        prefs.Settings.Editor.FontSize = 16;
        prefs.SaveFromPreferencesWindow();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(20, tab.State.Editor.FontSize);
        Assert.Equal(125, prefs.Scale.CodeZoomPercent);

        prefs.Scale.ResetCodeZoom();
        Assert.Equal(16, tab.State.Editor.FontSize);
        Assert.Equal(150, prefs.Scale.InterfaceScalePercent);

        prefs.Scale.SetCodeZoomPercent(110);
        prefs.Scale.ResetInterfaceScale();
        Assert.Equal(100, prefs.Settings.Appearance.InterfaceScalePercent);
        Assert.Equal(110, prefs.Settings.Editor.ZoomPercent);
        Assert.Equal(16, prefs.Settings.Editor.FontSize);
        Assert.Equal("Menlo", prefs.Settings.Editor.FontFamily);
        Assert.True(prefs.Settings.Editor.WordWrap);
        host.Close();
    }

    // Saved, Invoklet closed and opened again (a fresh settings object from the same JSON): both
    // multipliers come back, the interface scale on the window and the code zoom in the editor.
    // The editor's own FontSize carries only the code zoom; the window's transform adds the
    // interface scale once, so 12 × 1.25 × 1.10 = 16.5 logical units.
    [AvaloniaFact]
    public void AfterARestart_BothMultipliers_Should_ComeBack_WithoutScalingCodeTwice()
    {
        var before = new AppSettings();
        before.Appearance.InterfaceScalePercent = 125;
        before.Editor.ZoomPercent = 110;
        var prefs = new Prefs(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(before)));

        InterfaceScaling.Attach(prefs.Scale);
        try
        {
            var (tab, host) = OpenTab(prefs);

            Assert.Equal(1.25, InterfaceScaling.ScaleOf(host));
            Assert.Equal(12 * 1.1, tab.State.Editor.FontSize, 6);
            Assert.Equal("110%", ZoomButtonText(tab));
            var toWindow = tab.State.Editor.TransformToVisual(host)!.Value;
            Assert.Equal(1.25, toWindow.M11, 6);
            Assert.Equal(16.5, tab.State.Editor.FontSize * toWindow.M11, 6);
            host.Close();
        }
        finally
        {
            InterfaceScaling.Detach();
        }
    }

    [AvaloniaFact]
    public void AWindow_Should_ScaleItsContentAndSizes_AndFollowLaterChanges()
    {
        var prefs = new Prefs();
        prefs.Scale.SetInterfaceScalePercent(125);
        InterfaceScaling.Attach(prefs.Scale);
        try
        {
            var text = new TextBlock { Text = "Scaled" };
            var window = new Window { Width = 400, Height = 300, MinWidth = 200, MinHeight = 100, Content = text };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1.25, InterfaceScaling.ScaleOf(window));
            Assert.Equal(1.25, text.TransformToVisual(window)!.Value.M11, 6);
            Assert.Equal((500, 375, 250, 125), (window.Width, window.Height, window.MinWidth, window.MinHeight));

            prefs.Scale.SetInterfaceScalePercent(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, text.TransformToVisual(window)!.Value.M11, 6);
            Assert.Equal((600, 450, 300, 150), (window.Width, window.Height, window.MinWidth, window.MinHeight));

            prefs.Scale.ResetInterfaceScale();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, text.TransformToVisual(window)!.Value.M11, 6);
            Assert.Equal((400, 300, 200, 100), (window.Width, window.Height, window.MinWidth, window.MinHeight));
            window.Close();
        }
        finally
        {
            InterfaceScaling.Detach();
        }
    }

    // Menus, tooltips and flyouts open in popups of their own; they take the scale of the control
    // they open from.
    [AvaloniaFact]
    public void AContextMenu_Should_OpenAtTheInterfaceScale()
    {
        var prefs = new Prefs();
        prefs.Scale.SetInterfaceScalePercent(150);
        InterfaceScaling.Attach(prefs.Scale);
        try
        {
            var target = new Button { Content = "Target" };
            var window = new Window { Width = 400, Height = 300, Content = target };
            window.Show();
            var item = new MenuItem { Header = "Item" };
            var menu = new ContextMenu { ItemsSource = new[] { item } };
            target.ContextMenu = menu;
            menu.Open(target);
            Dispatcher.UIThread.RunJobs();

            var popupRoot = TopLevel.GetTopLevel(item);
            Assert.NotNull(popupRoot);
            Assert.Equal(1.5, item.TransformToVisual(popupRoot)!.Value.M11, 6);
            menu.Close();
            window.Close();
        }
        finally
        {
            InterfaceScaling.Detach();
        }
    }

    [AvaloniaFact]
    public void AClosedTab_Should_StopFollowingTheZoom()
    {
        var prefs = new Prefs();
        var (tab, host) = OpenTab(prefs);
        host.Close();
        tab.Dispose();

        prefs.Scale.SetCodeZoomPercent(200);

        Assert.Equal(12, tab.State.Editor.FontSize);
    }

    // Preferences → Interface offers the scale with a reset; Preferences → Editor shows the code zoom
    // (changed elsewhere) with its own reset.
    [AvaloniaFact]
    public void ThePreferencesWindow_Should_SetAndResetEachSettingOnItsOwn()
    {
        var prefs = new Prefs();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        prefs.Mock.Setup(p => p.Experimental).Returns(new ExperimentalSettings());
        using var vm = new Invoklet.Windows.Models.PreferencesWindowViewModel(theme.Object, prefs.Mock.Object, new LanguageHostRegistry());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Contains(125, vm.InterfaceScaleOptions);
        vm.InterfaceScalePercent = 125;
        prefs.Scale.ZoomIn(); // the wheel in some editor, while Preferences is open

        Assert.Equal(125, prefs.Settings.Appearance.InterfaceScalePercent);
        Assert.Equal(110, vm.CodeZoomPercent);
        Assert.Contains(nameof(vm.CodeZoomPercent), changed);

        vm.ResetCodeZoomCommand.Execute().Subscribe();
        Assert.Equal((125, 100), (vm.InterfaceScalePercent, vm.CodeZoomPercent));

        prefs.Scale.SetCodeZoomPercent(150);
        vm.ResetInterfaceScaleCommand.Execute().Subscribe();
        Assert.Equal((100, 150), (vm.InterfaceScalePercent, vm.CodeZoomPercent));
    }

    // Preferences is designed at 760×480 with a 640×420 minimum. At 200% on a 1280×800 laptop screen
    // (a 1280×775 work area below the macOS menu bar) the scaled minimum, 1280×840, used to be forced
    // on the window although it can't fit: the window could neither fit nor be made smaller. The
    // window must stay inside the work area, frame included, with its content scrolling instead, and
    // come back to its designed sizes at 100%.
    [AvaloniaFact]
    public void AScaledDialog_Should_StayInsideASmallWorkArea_AndScroll_ThenRestoreAt100Percent()
    {
        var prefs = new Prefs();
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        prefs.Mock.Setup(p => p.Experimental).Returns(new ExperimentalSettings());
        prefs.Scale.SetInterfaceScalePercent(200);
        var workArea = new Size(1280, 775);
        InterfaceScaling.WorkAreaOverride = _ => workArea;
        InterfaceScaling.Attach(prefs.Scale);
        try
        {
            using var vm = new Invoklet.Windows.Models.PreferencesWindowViewModel(theme.Object, prefs.Mock.Object, new LanguageHostRegistry());
            var window = new Invoklet.Windows.PreferencesWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = window.FrameSize ?? window.ClientSize;
            var chrome = new Size(frame.Width - window.ClientSize.Width, frame.Height - window.ClientSize.Height);
            Assert.True(window.MinWidth + chrome.Width <= workArea.Width && window.MinHeight + chrome.Height <= workArea.Height,
                $"minimum {window.MinWidth}×{window.MinHeight} (+ frame {chrome}) exceeds the {workArea} work area");
            // Strictly smaller: a window exactly filling the work area counts as zoomed on macOS and
            // would then be left out of later scale changes.
            Assert.True(window.Width + chrome.Width < workArea.Width && window.Height + chrome.Height < workArea.Height,
                $"size {window.Width}×{window.Height} (+ frame {chrome}) exceeds the {workArea} work area");
            Assert.Equal(2, InterfaceScaling.ScaleOf(window));

            // The content keeps its designed minimum (in its own units) and scrolls to the rest of it.
            Assert.True(InterfaceScaling.IsContentScrolling(window));
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height, $"extent {scroller.Extent}, viewport {scroller.Viewport}");
            Assert.True(scroller.Extent.Width >= 640 && scroller.Extent.Height >= 420, $"extent {scroller.Extent}");

            // 150% fits that screen (its 720 height just meets the edge margin): no scrolling.
            prefs.Scale.SetInterfaceScalePercent(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((960, 630, 1140, 719), (window.MinWidth, window.MinHeight, window.Width, window.Height));
            Assert.False(InterfaceScaling.IsContentScrolling(window));

            prefs.Scale.ResetInterfaceScale();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, prefs.Scale.InterfaceScalePercent);
            Assert.Equal((640, 420, 760, 480), (window.MinWidth, window.MinHeight, window.Width, window.Height));
            Assert.False(InterfaceScaling.IsContentScrolling(window));
            Assert.Equal(1, InterfaceScaling.ScaleOf(window));
            window.Close();
        }
        finally
        {
            InterfaceScaling.Detach();
            InterfaceScaling.WorkAreaOverride = null;
        }
    }

    // A window is resized in a dispatcher job of its own, after the change that caused it (a key
    // press in Preferences, each step of a held arrow key) is over — resized inside that chain, a
    // macOS window could keep a drawing surface of its old size. The content's transform follows at
    // once; quick successive changes resize the window once, to the last scale.
    [AvaloniaFact]
    public void LiveScaleChanges_Should_ResizeWindowsLater_AndOnlyToTheLastScale()
    {
        var prefs = new Prefs();
        InterfaceScaling.Attach(prefs.Scale);
        try
        {
            var text = new TextBlock { Text = "Scaled" };
            var window = new Window { Width = 400, Height = 300, MinWidth = 200, MinHeight = 100, Content = text };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var resizes = new List<Size>();
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Layoutable.WidthProperty) resizes.Add(new Size(window.Width, window.Height));
            };

            prefs.Scale.SetInterfaceScalePercent(110);
            prefs.Scale.SetInterfaceScalePercent(125);

            Assert.Equal(1.25, text.TransformToVisual(window)!.Value.M11, 6);
            Assert.Equal((400, 300), (window.Width, window.Height));
            Assert.Empty(resizes);

            Dispatcher.UIThread.RunJobs();
            Assert.Equal((500, 375, 250, 125), (window.Width, window.Height, window.MinWidth, window.MinHeight));
            Assert.Single(resizes);
            window.Close();
        }
        finally
        {
            InterfaceScaling.Detach();
        }
    }
}
