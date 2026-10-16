using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RunPad.Avalonia.Themes;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The default button of a dialog (the one Enter presses) is its primary action and is drawn in the
// theme's accent with readable text in every state; splitters draw a 1-DIP line inside a wider drag
// area. These run the real RunPadTheme styles and resources.
public class PrimaryActionAndSplitterTests
{
    public static TheoryData<string> AllThemes() =>
        new(typeof(RunPadThemeVariants).GetFields().Where(f => f.FieldType == typeof(ThemeVariant)).Select(f => f.Name));

    private static ThemeVariant Variant(string name) => (ThemeVariant)typeof(RunPadThemeVariants).GetField(name)!.GetValue(null)!;

    private static T Themed<T>(T window, string theme = "VsCodeDark") where T : Window
    {
        window.Styles.Add(new RunPadTheme());
        window.RequestedThemeVariant = Variant(theme);
        return window;
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    // Full precision, no rounding.
    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void PrimaryButtonAndSelection_Should_KeepTextAt4Point5To1_InEveryState(string themeName)
    {
        var theme = new RunPadTheme();
        Color Get(string key)
        {
            Assert.True(theme.TryGetResource(key, Variant(themeName), out var value), $"{key} missing in {themeName}");
            return ((ISolidColorBrush)value!).Color;
        }

        var text = Get("RunPad.Button.Primary.ForegroundBrush");
        foreach (var state in new[] { "BackgroundBrush", "HoverBrush", "PressedBrush" })
        {
            var ratio = Contrast(text, Get("RunPad.Button.Primary." + state));
            Assert.True(ratio >= 4.5, $"{themeName} primary {state}: {ratio:0.0000}");
        }

        var selection = Contrast(Get("RunPad.Accent.ForegroundBrush"), Get("RunPad.Accent.BackgroundBrush"));
        Assert.True(selection >= 4.5, $"{themeName} accent (selection) text: {selection:0.0000}");
    }

    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    [InlineData("RunPadMidnight")]
    [InlineData("RiderLight")]
    public void TheDefaultButton_Should_UseThePrimaryBrushes_InEachState_AndOthersShouldNot(string themeName)
    {
        var dialog = Themed(new ConditionDialog("x > 1"), themeName);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var ok = buttons.Single(b => b.IsDefault);
        var cancel = buttons.Single(b => b.IsCancel);
        IBrush Brush(string key) { dialog.TryFindResource(key, dialog.ActualThemeVariant, out var v); return (IBrush)v!; }

        Assert.Same(Brush("RunPad.Button.Primary.BackgroundBrush"), Presenter(ok).Background);
        Assert.Same(Brush("RunPad.Button.Primary.ForegroundBrush"), Presenter(ok).Foreground);
        Assert.NotSame(Brush("RunPad.Button.Primary.BackgroundBrush"), Presenter(cancel).Background);

        ((IPseudoClasses)ok.Classes).Add(":pointerover");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(Brush("RunPad.Button.Primary.HoverBrush"), Presenter(ok).Background);
        ((IPseudoClasses)ok.Classes).Add(":pressed");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(Brush("RunPad.Button.Primary.PressedBrush"), Presenter(ok).Background);
        ((IPseudoClasses)ok.Classes).Remove(":pressed");
        ((IPseudoClasses)ok.Classes).Remove(":pointerover");

        // Keyboard focus: a ring in the text colour inside the accent.
        ((IPseudoClasses)ok.Classes).Add(":focus-visible");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(Brush("RunPad.Button.Primary.ForegroundBrush"), Presenter(ok).BorderBrush);
        Assert.Equal(new Thickness(2), Presenter(ok).BorderThickness);
        ((IPseudoClasses)ok.Classes).Remove(":focus-visible");

        // Disabled keeps the ordinary (Fluent) disabled look instead of a live-looking accent.
        ok.IsEnabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(Brush("RunPad.Button.Primary.BackgroundBrush"), Presenter(ok).Background);
        dialog.Close();
    }

    // The look changed, the keys didn't: Enter is OK, Escape is Cancel.
    [AvaloniaTheory]
    [InlineData(Key.Enter, true)]
    [InlineData(Key.Escape, false)]
    public async Task EnterAndEscape_Should_StillChooseTheDefaultAndCancelButtons(Key key, bool expectResult)
    {
        var owner = Themed(new Window());
        owner.Show();
        var dialog = Themed(new ConditionDialog("count > 10", "= 5"));
        var result = dialog.ShowDialog<(string, string, bool)?>(owner);
        Dispatcher.UIThread.RunJobs();

        dialog.KeyPress(key, RawInputModifiers.None, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Escape, "");
        Dispatcher.UIThread.RunJobs();

        Assert.True(result.IsCompleted, "the dialog didn't close");
        Assert.Equal(expectResult, (await result).HasValue);
        if (expectResult) Assert.Equal(("count > 10", "= 5", true), (await result)!.Value);
        owner.Close();
    }

    // The seam reserves one DIP in layout. Its five-DIP target overlaps both panels and still
    // resizes from the edge of that target with either the mouse or keyboard.
    [AvaloniaTheory]
    [InlineData(GridResizeDirection.Columns)]
    [InlineData(GridResizeDirection.Rows)]
    public void ASeam_Should_ReserveOneDip_OverlapThePanels_AndResize(GridResizeDirection direction)
    {
        var columns = direction == GridResizeDirection.Columns;
        var plain = SplitterIn(new GridSplitter { ResizeDirection = direction }, columns, out var plainWindow);
        var plainArea = plain.Bounds;
        HoverOver(plain, plainWindow, columns);
        var plainCursor = plain.Cursor;
        plainWindow.Close();

        var splitter = SplitterIn(new GridSplitter { Classes = { "seam" }, ResizeDirection = direction }, columns, out var window);
        var grid = (Grid)splitter.Parent!;
        var line = splitter.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Line");
        var area = columns ? splitter.Bounds.Width : splitter.Bounds.Height;
        Assert.True(columns ? plainArea.Width >= 5 : plainArea.Height >= 5);
        Assert.Equal(1, columns ? grid.ColumnDefinitions[1].ActualWidth : grid.RowDefinitions[1].ActualHeight);
        Assert.Equal(198, columns ? splitter.Bounds.X : splitter.Bounds.Y);
        Assert.True(area >= 5, $"drag area {area}");
        Assert.Equal(1, columns ? line.Bounds.Width : line.Bounds.Height);
        Assert.Equal(Math.Floor((area - 1) / 2), columns ? line.Bounds.X : line.Bounds.Y, 0);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)splitter.Background!).Color);
        HoverOver(splitter, window, columns);
        Assert.NotNull(plainCursor);
        Assert.Equal(plainCursor!.ToString(), splitter.Cursor?.ToString()); // the resize cursor, as for a plain splitter

        // Drag from the drag area's edge (outside the 1-DIP line) by 40 DIP.
        var start = splitter.TranslatePoint(new Point(columns ? 0.5 : 300, columns ? 250 : 0.5), window)!.Value;
        var delta = columns ? new Vector(40, 0) : new Vector(0, 40);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + delta / 2);
        window.MouseMove(start + delta);
        window.MouseUp(start + delta, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var size = columns ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
        Assert.InRange(size, 239, 241);
        splitter.Focus(NavigationMethod.Tab);
        window.KeyPress(columns ? Key.Right : Key.Down, RawInputModifiers.None,
            columns ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown, "");
        Dispatcher.UIThread.RunJobs();
        var afterKey = columns ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
        Assert.True(afterKey > size, "Keyboard resizing must still move the seam.");
        window.Close();
    }

    private static void HoverOver(GridSplitter splitter, Window window, bool columns)
    {
        window.MouseMove(splitter.TranslatePoint(new Point(columns ? 0.5 : 300, columns ? 250 : 0.5), window)!.Value);
        Dispatcher.UIThread.RunJobs();
    }

    private static GridSplitter SplitterIn(GridSplitter splitter, bool columns, out Window window)
    {
        var grid = new Grid();
        var first = new Border();
        if (columns)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("200,1,*");
            Grid.SetColumn(splitter, 1);
            if (!splitter.Classes.Contains("seam")) splitter.Width = 5;
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions("200,1,*");
            Grid.SetRow(splitter, 1);
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        grid.Children.Add(first);
        grid.Children.Add(splitter);
        window = Themed(new Window { Width = 600, Height = 500, Content = grid });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return splitter;
    }

    private static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            static double Lin(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
