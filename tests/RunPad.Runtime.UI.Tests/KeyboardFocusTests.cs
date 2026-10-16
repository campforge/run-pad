using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.TemplatedControls;
using RunPad.UserControls;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

/// <summary>
/// Fluent's keyboard-focus frame (its focus adorner) around a control — or around one of its own
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

// One keyboard-focus look: Fluent's frame (white in dark themes, black in light ones), shown when a
// control is reached with the keyboard and not when it is clicked. Before, combo boxes filled
// themselves with the accent instead and the side panel's and panels' tabs drew a second, accent
// frame inside Fluent's. Real windows and real keyboard and mouse input.
public class KeyboardFocusTests
{
    private static MainWindow MainWindow(string theme, out MainWindowViewModel vm)
    {
        _ = typeof(RunPad.App).Assembly;
        var root = Directory.CreateTempSubdirectory("runpad-focus-test").FullName;
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.Folder).Returns(root);
        IReadOnlyList<FileSystemItem> tree = [new("Notes", true, Path.Combine(root, "Notes"), []), new("a.rpad", false, Path.Combine(root, "a.rpad"), null)];
        fs.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tree);
        var themeService = new Mock<IThemeService>();
        themeService.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true), new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        vm = new MainWindowViewModel([], fs.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(themeService.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().Object,
            prefs.Object, () => new Mock<IIntelliSenseService>().Object, new CSharpReferenceService(), new Mock<IBuildCacheService>().Object);
        var window = new MainWindow(prefs.Object, tabs, vm) { Width = 1100, Height = 700 };
        Themed(window, theme);
        foreach (var x in new[] { "TemplatedControls/IconButton.axaml", "TemplatedControls/IconToggleButton.axaml", "TemplatedControls/ToolbarComboBox.axaml" })
            window.Styles.Add(new StyleInclude(new Uri("avares://RunPad/")) { Source = new Uri("avares://RunPad/" + x) });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://RunPad/")) { Source = new Uri("avares://RunPad/Resources/Icons.axaml") });
        return window;
    }

    private static T Themed<T>(T window, string theme) where T : Window
    {
        window.Styles.Add(new RunPadTheme());
        window.RequestedThemeVariant = (ThemeVariant)typeof(RunPadThemeVariants).GetField(theme)!.GetValue(null)!;
        return window;
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

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

    // Tab goes through the side panel, the tree, the document tab and the toolbar: every stop shows
    // exactly one frame — Fluent's — in the theme's focus colour, and nothing draws a second one.
    [AvaloniaTheory]
    [InlineData("VsCodeDark", "#FFFFFFFF")]
    [InlineData("VsCodeLight", "#FF000000")]
    [InlineData("RunPadWarmLight", "#FF000000")]
    public async Task EveryKeyboardStop_InTheMainWindow_Should_ShowOneFrame(string theme, string expected)
    {
        var window = MainWindow(theme, out var vm);
        window.Show();
        vm.OpenExample(vm.Examples[0]);
        await SettleAsync();
        var start = window.GetVisualDescendants().OfType<TabStripItem>().First();
        Click(window, start);
        Assert.False(FocusFrames.IsShown(start));

        var seen = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            Tab(window);
            var focused = (Control)window.FocusManager!.GetFocusedElement()!;
            if (focused is AvaloniaEdit.Editing.TextArea) break; // the editor keeps Tab for indenting
            seen.Add(focused.GetType().Name);
            Assert.True(FocusFrames.IsShown(focused), $"{focused.GetType().Name} has no keyboard-focus frame");
            Assert.Single(FocusFrames.Of(focused));
            Assert.Equal(Color.Parse(expected), FrameColor(focused));
            Assert.DoesNotContain(focused.GetVisualDescendants().OfType<Border>(), b => b.Name == "FocusFrame");
            // and no second, filled focus look (Fluent's ComboBox accent fill)
            Assert.DoesNotContain(focused.GetVisualDescendants().OfType<Border>(), b => b.Name == "HighlightBackground" && b.IsEffectivelyVisible);
        }

        Assert.Contains(nameof(TabStripItem), seen);
        Assert.Contains(nameof(TreeViewItem), seen);
        Assert.Contains(nameof(TabItem), seen);
        Assert.Contains(nameof(IconButton), seen);
        Assert.Contains(nameof(ToolbarComboBox), seen);
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
            window.GetVisualDescendants().OfType<TabStripItem>().Last(),
            window.GetVisualDescendants().OfType<TabItem>().First(),
            window.GetVisualDescendants().OfType<ToolbarComboBox>().First(),
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
        var dialog = Themed(new ConditionDialog("count > 10", "= 5"), "VsCodeDark");
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var check = dialog.FindControl<CheckBox>("EnabledBox")!;

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
                Assert.False(FocusFrames.IsShown(focused)); // text fields: their own focused border
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
}
