using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.TemplatedControls;
using Invoklet.UserControls;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows;

/// <summary>
/// The theme's keyboard-focus frame (its focus adorner) around a control — or around one of its own
/// parts: a tree item frames its row, not its children.
/// </summary>
internal static class FocusFrames
{
    public static IEnumerable<Control> Of(Control control) =>
        AdornerLayer.GetAdornerLayer(control)?.Children.OfType<Control>().Where(a =>
            AdornerLayer.GetAdornedElement(a) is { } adorned
            && (adorned == control || adorned.GetVisualAncestors().TakeWhile(v => v is not ItemsControl || v == control).Contains(control))) ?? [];

    public static bool IsShown(Control control) => Of(control).Any(a => a.IsVisible);
}

// One keyboard-focus look from the active theme, shown when a
// control is reached with the keyboard and not when it is clicked. Before, combo boxes filled
// themselves with the accent instead and the side panel's and panels' tabs drew a second, accent
// frame inside Fluent's. Real windows and real keyboard and mouse input.
public class KeyboardFocusTests
{
    private static MainWindow MainWindow(string theme, out MainWindowViewModel vm)
    {
        _ = typeof(Invoklet.App).Assembly;
        var root = Directory.CreateTempSubdirectory("Invoklet-focus-test").FullName;
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.Folder).Returns(root);
        IReadOnlyList<FileSystemItem> tree = [new("Notes", true, Path.Combine(root, "Notes"), []), new("a.inv", false, Path.Combine(root, "a.inv"), null)];
        fs.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tree);
        var themeService = new Mock<IThemeService>();
        themeService.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true), new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        vm = new MainWindowViewModel([], fs.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(themeService.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object),
            prefs.Object, new Mock<IBuildCacheService>().Object);
        var window = new MainWindow(prefs.Object, tabs, vm) { Width = 1100, Height = 700 };
        Themed(window, theme);
        foreach (var x in new[] { "TemplatedControls/IconButton.axaml", "TemplatedControls/IconToggleButton.axaml", "TemplatedControls/ToolbarComboBox.axaml" })
            window.Styles.Add(new StyleInclude(new Uri("avares://Invoklet/")) { Source = new Uri("avares://Invoklet/" + x) });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Invoklet/")) { Source = new Uri("avares://Invoklet/Resources/Icons.axaml") });
        return window;
    }

    private static T Themed<T>(T window, string theme) where T : Window
    {
        window.Styles.Add(new InvokletTheme());
        window.RequestedThemeVariant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(theme)!.GetValue(null)!;
        return window;
    }

    // Runs what the dispatcher has queued (layout, focus), a few rounds because a job can queue the next one;
    // no waiting on the clock.
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    private static async Task<T> WaitForControlAsync<T>(Window window, Func<T, bool>? predicate = null)
        where T : Control
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var control = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => predicate?.Invoke(c) ?? true);
            if (control is not null)
                return control;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {typeof(T).Name}.");
    }

    // A side-panel section header shown in the window (Expander.bundle in MainWindow), which replaced the
    // side panel's tab strip. Connections is hidden while there are none, as in these tests.
    private static bool IsSectionHeader(ToggleButton button) => button.Classes.Contains("bundle-header") && button.IsEffectivelyVisible;

    private static void Tab(Window w)
    {
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "");
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window w, Control c)
    {
        var p = c.TranslatePoint(new Point(4, 4), w)!.Value;
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Color FrameColor(Control c) =>
        ((ISolidColorBrush)FocusFrames.Of(c).SelectMany(a => new[] { (Visual)a }.Concat(a.GetVisualDescendants())).OfType<Border>().First().BorderBrush!).Color;

    // Tab goes through the side panel's sections and trees, the document tab and the toolbar: every stop shows
    // exactly one frame in the theme's focus colour, and nothing draws a second one.
    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    [InlineData("InvokletWarmLight")]
    [InlineData("RiderDark")]
    [InlineData("RiderLight")]
    [InlineData("Vs2022Dark")]
    [InlineData("Vs2022Light")]
    [InlineData("InvokletMidnight")]
    public async Task EveryKeyboardStop_InTheMainWindow_Should_ShowOneFrame(string theme)
    {
        var window = MainWindow(theme, out var vm);
        window.Show();
        vm.OpenExample(vm.Examples[0]);
        await SettleAsync();
        // From the top of the side panel: its other section headers and its trees come before the document tab.
        var start = await WaitForControlAsync<ToggleButton>(window, IsSectionHeader);
        // A click folds the section; the second one opens it again, so its tree is still there for Tab.
        Click(window, start);
        Click(window, start);
        Assert.True(start.IsChecked);
        Assert.False(FocusFrames.IsShown(start));

        var seen = new List<Control>();
        for (var i = 0; i < 20; i++)
        {
            Tab(window);
            var focused = (Control)window.FocusManager!.GetFocusedElement()!;
            if (focused is global::AvaloniaEdit.Editing.TextArea) break; // the editor keeps Tab for indenting
            seen.Add(focused);
            Assert.True(FocusFrames.IsShown(focused), $"{focused.GetType().Name} has no keyboard-focus frame");
            Assert.Single(FocusFrames.Of(focused));
            focused.TryFindResource("Invoklet.Focus.BorderBrush", focused.ActualThemeVariant, out var brush);
            Assert.Equal(((ISolidColorBrush)brush!).Color, FrameColor(focused));
            Assert.DoesNotContain(focused.GetVisualDescendants().OfType<Border>(), b => b.Name == "FocusFrame");
            // and no second, filled focus look (Fluent's ComboBox accent fill)
            Assert.DoesNotContain(focused.GetVisualDescendants().OfType<Border>(), b => b.Name == "HighlightBackground" && b.IsEffectivelyVisible);
        }

        Assert.Contains(seen, c => c is ToggleButton t && IsSectionHeader(t));
        Assert.Contains(seen, c => c is TreeViewItem);
        Assert.Contains(seen, c => c is TabItem);
        Assert.Contains(seen, c => c is IconButton);
        Assert.Contains(seen, c => c is ToolbarComboBox);
        window.Close();
    }

    // The same controls, clicked: focus moves, no frame.
    [AvaloniaFact]
    public async Task Clicking_Should_NotShowTheFrame()
    {
        var window = MainWindow("VsCodeDark", out var vm);
        window.Show();
        vm.OpenExample(vm.Examples[0]);
        await SettleAsync();
        var targets = new Control[]
        {
            await WaitForControlAsync<ToggleButton>(window, IsSectionHeader),
            await WaitForControlAsync<TabItem>(window),
            await WaitForControlAsync<ToolbarComboBox>(window),
        };

        foreach (var target in targets)
        {
            Click(window, target);
            if (target is ComboBox combo) combo.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused || target.IsKeyboardFocusWithin, $"{target.GetType().Name} didn't take focus");
            Assert.False(FocusFrames.IsShown(target), $"{target.GetType().Name} shows a frame after a click");
        }

        // and the keyboard brings it back on the next control
        Tab(window);
        Assert.True(FocusFrames.IsShown((Control)window.FocusManager!.GetFocusedElement()!));
        window.Close();
    }

    // A dialog: check box and buttons get the frame from Tab, the text field shows its focused
    // border (and caret) either way; a click on the check box shows no frame.
    [AvaloniaFact]
    public void DialogControls_Should_ShowTheFrameOnlyFromTheKeyboard()
    {
        var dialog = Themed(new Window { Content = new StackPanel { Children = { new CheckBox { Content = "Enabled" }, new TextBox { Text = "count > 10" }, new TextBox { Text = "= 5" }, new Button { Content = "Done" }, new Button { Content = "Cancel" } } } }, "VsCodeDark");
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var check = dialog.GetVisualDescendants().OfType<CheckBox>().Single();

        Click(dialog, check);
        Assert.True(check.IsFocused);
        Assert.False(FocusFrames.IsShown(check));

        var reached = new List<Control>();
        for (var i = 0; i < 4; i++)
        {
            Tab(dialog);
            var focused = (Control)dialog.FocusManager!.GetFocusedElement()!;
            reached.Add(focused);
            if (focused is Button)
                Assert.True(FocusFrames.IsShown(focused), $"{((Button)focused).Content} has no frame");
            else
                Assert.True(FocusFrames.IsShown(focused)); // text fields share the same ring
        }

        Assert.Equal([typeof(TextBox), typeof(TextBox), typeof(Button), typeof(Button)], reached.Select(c => c.GetType()));
        Tab(dialog);
        Assert.Same(check, dialog.FocusManager!.GetFocusedElement());
        Assert.True(FocusFrames.IsShown(check));
        dialog.Close();
    }

    // An ordinary combo box (dialogs, Preferences): the same frame from the keyboard, no accent fill
    // over its text, nothing on a click.
    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    public void AComboBox_Should_ShowTheSameFrame_WithoutTheAccentFill(string theme)
    {
        var before = new Button { Content = "Before" };
        var combo = new ComboBox { ItemsSource = new[] { "Statements", "Program" }, SelectedIndex = 0 };
        var window = Themed(new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { before, combo } } }, theme);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        bool Filled() => combo.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "HighlightBackground" && b.IsEffectivelyVisible);

        Click(window, combo);
        combo.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(combo.IsFocused);
        Assert.False(FocusFrames.IsShown(combo));
        Assert.False(Filled());

        Click(window, before);
        Tab(window);
        Assert.True(combo.IsFocused);
        Assert.True(FocusFrames.IsShown(combo));
        Assert.False(Filled());
        window.Close();
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
    public void Controls_Should_ShareThemeGeometry_AndKeepTheRingVisible(string theme)
    {
        var controls = new Control[]
        {
            new Button { Content = "Action" },
            new Button { Content = "Default", IsDefault = true },
            new ToggleButton { Content = "Toggle" },
            new CheckBox { Content = "Check" },
            new RadioButton { Content = "Radio" },
            new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            new TextBox { Text = "Input" },
            new NumericUpDown { Value = 12 },
        };
        var panel = new StackPanel();
        foreach (var control in controls) panel.Children.Add(control);
        var window = Themed(new Window { Width = 400, Height = 500, Content = panel }, theme);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        object Resource(string key)
        {
            Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), key);
            return value!;
        }
        var primary = ((ISolidColorBrush)Resource("Invoklet.Focus.BorderBrush")).Color;
        var contrast = ((ISolidColorBrush)Resource("Invoklet.Focus.ContrastBrush")).Color;
        var radius = (CornerRadius)Resource("Invoklet.Focus.CornerRadius");
        var thickness = (Thickness)Resource("Invoklet.Focus.Thickness");
        controls[0].Focus(NavigationMethod.Tab);
        foreach (var control in controls)
        {
            var focused = (Control)window.FocusManager!.GetFocusedElement()!;
            Assert.True(control == focused || focused.GetVisualAncestors().Contains(control));
            var frames = FocusFrames.Of(focused).ToArray();
            Assert.Single(frames);
            var ring = frames[0].GetVisualDescendants().Prepend(frames[0]).OfType<Border>().Single(b => b.Name == "FocusRingPrimary");
            Assert.Equal(primary, ((ISolidColorBrush)ring.BorderBrush!).Color);
            Assert.Equal(radius, ring.CornerRadius);
            Assert.Equal(thickness, ring.BorderThickness);
            Tab(window);
        }
        foreach (var background in new[] { "Panel.BackgroundBrush", "Sidebar.BackgroundBrush", "Editor.BackgroundBrush",
            "Output.BackgroundBrush", "Toolbar.BackgroundBrush", "Accent.BackgroundBrush", "Button.Primary.BackgroundBrush",
            "Button.Primary.HoverBrush", "Button.Primary.PressedBrush" })
        {
            var colour = ((ISolidColorBrush)Resource("Invoklet." + background)).Color;
            Assert.True(Math.Max(Contrast(primary, colour), Contrast(contrast, colour)) >= 3,
                $"Focus ring is lost on {theme}/{background}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void FocusedControls_Should_FollowThemeChangesAndResourceOverrides()
    {
        var button = new Button { Content = "Action" };
        var window = Themed(new Window { Width = 400, Height = 200, Content = button }, "VsCodeLight");
        window.Show();
        button.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        var light = FrameColor(button);
        window.RequestedThemeVariant = InvokletThemeVariants.RiderDark;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(light, FrameColor(button));
        window.TryFindResource("Invoklet.Focus.BorderBrush", window.ActualThemeVariant, out var themed);
        Assert.Equal(((ISolidColorBrush)themed!).Color, FrameColor(button));

        window.Resources["Invoklet.Focus.BorderBrush"] = new SolidColorBrush(Colors.Orange);
        window.Resources["Invoklet.Focus.Thickness"] = new Thickness(3);
        window.Resources["Invoklet.Focus.CornerRadius"] = new CornerRadius(6);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Colors.Orange, FrameColor(button));
        var ring = FocusFrames.Of(button).SelectMany(f => f.GetVisualDescendants().Prepend(f)).OfType<Border>().Single(b => b.Name == "FocusRingPrimary");
        Assert.Equal(new Thickness(3), ring.BorderThickness);
        Assert.Equal(new CornerRadius(6), ring.CornerRadius);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    public async Task GridCells_Should_UseTheSameRing_WithoutSquareFocusRectangles(string theme)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            ItemsSource = new[] { new KeyValuePair<string, string>("One", "Two") },
            IsReadOnly = true,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Key", Binding = new Binding("Key") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding("Value") });
        var window = new Window { Width = 500, Height = 250, Content = grid };
        var gridStyles = new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        };
        Application.Current!.Styles.Add(gridStyles);
        try
        {
            Themed(window, theme);
            Assert.True(window.TryFindResource(typeof(DataGrid), window.RequestedThemeVariant, out var gridTheme), "DataGrid theme was not loaded");
            grid.Theme = Assert.IsType<ControlTheme>(gridTheme);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await SettleAsync();
            var cells = grid.GetVisualDescendants().OfType<DataGridCell>().ToArray();
            Assert.True(cells.Length >= 2, $"cells={cells.Length}, columns={grid.Columns.Count}, bounds={grid.Bounds}, visuals={string.Join(",", grid.GetVisualDescendants().Take(10).Select(v => v.GetType().Name))}");
            Click(window, cells[0]);
            Assert.False(FocusFrames.IsShown(cells[0]));
            Assert.True(cells[1].Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            var focused = (Control)window.FocusManager!.GetFocusedElement()!;
            Assert.IsType<DataGridCell>(focused);
            Assert.True(FocusFrames.IsShown(focused));
            Assert.DoesNotContain(focused.GetVisualDescendants().OfType<Grid>(), g => g.Name == "FocusVisual" && g.IsEffectivelyVisible);
            window.TryFindResource("Invoklet.Focus.BorderBrush", window.ActualThemeVariant, out var brush);
            Assert.Equal(((ISolidColorBrush)brush!).Color, FrameColor(focused));
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(gridStyles);
        }
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
