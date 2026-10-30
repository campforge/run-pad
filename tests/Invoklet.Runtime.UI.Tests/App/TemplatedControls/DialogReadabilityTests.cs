using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Readability of small dialogs and of secondary text: long text must be wrapped or visibly cut (not
// silently clipped at the window edge), the dialog's buttons must stay inside it, error details are
// body text, and secondary text keeps a 4.5:1 contrast in every theme.
public class DialogReadabilityTests
{
    private static readonly IBrush Primary = new SolidColorBrush(Colors.Black);
    private static readonly IBrush Error = new SolidColorBrush(Colors.Red);

    private static T Show<T>(T window) where T : Window
    {
        window.Resources["Invoklet.Text.PrimaryBrush"] = Primary;
        window.Resources["Invoklet.Status.ErrorBrush"] = Error;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // The control's rectangle in the window's client area.
    private static Rect InWindow(Window window, Control control) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void AssertInside(Window window, Control control, string what)
    {
        var r = InWindow(window, control);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= window.ClientSize.Width + 0.5 && r.Bottom <= window.ClientSize.Height + 0.5,
            $"{what} {r} is outside the {window.ClientSize} window");
    }

    [AvaloniaFact]
    public void BreakpointConditionEditor_Should_WrapErrors_AndKeepButtonsInsideAtItsDesignedWidth()
    {
        using var panel = new Invoklet.UserControls.BreakpointConditionEditor();
        var window = Show(new Window { Width = 420, Height = 420, Content = panel });
        try
        {
            var error = panel.FindControl<TextBlock>("ErrorText")!;
            error.Text = new string('x', 180);
            error.IsVisible = true;
            window.UpdateLayout();
            Assert.True(error.TextLayout.TextLines.Count > 1);
            AssertInside(window, error, "condition error");
            AssertInside(window, panel.FindControl<Button>("DoneButton")!, "Done");
            Assert.Null(panel.FindControl<Expander>("HitCountOptions"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AboutWindow_Should_CutALongVersionVisibly_AndOfferAllOfIt()
    {
        var about = Show(new AboutWindow());
        var version = about.FindControl<TextBlock>("VersionBlock")!;
        var full = "Version 0.5.0+dcd8779d2bc3ebb63369039a5653f2bfb786bdb7-very-long-build-metadata";
        version.Text = full;
        ToolTip.SetTip(version, full); // what the window does with its own version
        Dispatcher.UIThread.RunJobs();

        AssertInside(about, version, "version");
        Assert.Contains(version.TextLayout.TextLines, l => l.HasCollapsed);
        Assert.Equal(360, about.Width);
        foreach (var button in about.GetVisualDescendants().OfType<Button>())
            AssertInside(about, button, $"button {button.Content}");
        about.Close();
    }

    [AvaloniaFact]
    public void AboutWindow_Should_OfferItsOwnFullVersion_AsATooltip()
    {
        var about = Show(new AboutWindow());
        var version = about.FindControl<TextBlock>("VersionBlock")!;

        Assert.Equal(version.Text, ToolTip.GetTip(version));
        Assert.StartsWith("Version ", version.Text);
        about.Close();
    }

    [AvaloniaFact]
    public void ErrorDialog_Should_ShowWhatFailed_AndTheDetailsAsBodyText()
    {
        var message = string.Join(' ', Enumerable.Repeat("The file could not be written: access to the path is denied.", 4));
        var dialog = Show(new ErrorDialog("Cannot save", message));
        var texts = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        var title = texts.Single(t => t.Text == "Cannot save");
        var body = texts.Single(t => t.Text == message);

        Assert.Same(Primary, body.Foreground);
        Assert.NotSame(Error, title.Foreground);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        AssertInside(dialog, body, "message");
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>())
            AssertInside(dialog, button, $"button {button.Content}");
        dialog.Close();
    }

    public static TheoryData<string> AllThemes() =>
        new(typeof(InvokletThemeVariants).GetFields().Where(f => f.FieldType == typeof(ThemeVariant)).Select(f => f.Name));

    // Full precision, no rounding: 4.48 is a failure even though it rounds to 4.5.
    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void SecondaryText_Should_Have4Point5To1Contrast_OnPanelAndSidebar(string themeName)
    {
        var variant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(themeName)!.GetValue(null)!;
        var theme = new InvokletTheme();
        Color Get(string key)
        {
            Assert.True(theme.TryGetResource(key, variant, out var value), $"{key} missing in {themeName}");
            return ((ISolidColorBrush)value!).Color;
        }

        var secondary = Get("Invoklet.Text.SecondaryBrush");
        var primary = Get("Invoklet.Text.PrimaryBrush");
        foreach (var background in new[] { "Invoklet.Panel.BackgroundBrush", "Invoklet.Sidebar.BackgroundBrush" })
        {
            var bg = Get(background);
            var ratio = Contrast(secondary, bg);
            Assert.True(ratio >= 4.5, $"{themeName}: secondary {secondary} on {background} {bg} = {ratio:0.0000}");
            // Still a step below primary text.
            Assert.True(Contrast(primary, bg) > ratio + 2, $"{themeName}: primary and secondary too close on {background}");
        }
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
