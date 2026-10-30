using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The buttons of the dialogs are as tall as the fields beside them (the theme's control height), whatever the dialog sets or
// leaves out, so a row of buttons and the buttons of two dialogs line up.
public class DialogButtonHeightTests
{
    public static TheoryData<string> Dialogs => new("error", "newfile", "goto", "reload", "revert", "exit", "conflict", "unsaved", "rename", "connection", "about");

    private static Window Create(string kind) => kind switch
    {
        "error" => new ErrorDialog("Title", "A message"),
        "newfile" => new NewFileKindDialog(),
        "goto" => new GoToLineDialog(1, 10),
        "reload" => ReloadFileDialog.ChangedOnDisk("a.inv"),
        "revert" => ReloadFileDialog.Revert("a.inv"),
        "exit" => new ExitUnsavedDialog(["a.inv", "b.inv"]),
        "conflict" => new SaveConflictDialog("a.inv"),
        "unsaved" => new UnsavedChangesDialog("a.inv"),
        "rename" => new RenameSymbolDialog("Name"),
        "connection" => new ConnectionPackageConfirmationDialog("Add it?"),
        _ => new AboutWindow(),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public void EveryButtonOfADialog_Should_BeAsTallAsTheThemesControlHeight(string kind)
    {
        var theme = new InvokletTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var window = Create(kind);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            theme.TryGetResource("Invoklet.Size.Control", null, out var expected);

            var heights = window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible)
                .Select(b => $"{b.Name ?? b.Content}: {b.Bounds.Height}").ToList();

            Assert.NotEmpty(heights);
            Assert.All(window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible),
                b => Assert.True(b.Bounds.Height == (double)expected!, $"{kind}: {string.Join("; ", heights)}"));
        }
        finally
        {
            window.Close();
            global::Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    // A button's own alignment is Stretch, which leaves a label at the top left of a button taller than its text: the form
    // buttons of the theme centre it, so a label sits in the middle whatever height the dialog gives its button.
    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public void EveryButtonOfADialog_Should_CentreItsLabelBothWays(string kind)
    {
        var theme = new InvokletTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var window = Create(kind);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var buttons = window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();

            Assert.NotEmpty(buttons);
            Assert.All(buttons, b =>
            {
                Assert.True(b.HorizontalContentAlignment == HorizontalAlignment.Center, $"{kind}: {b.Name ?? b.Content}: {b.HorizontalContentAlignment}");
                Assert.True(b.VerticalContentAlignment == VerticalAlignment.Center, $"{kind}: {b.Name ?? b.Content}: {b.VerticalContentAlignment}");
            });
        }
        finally
        {
            window.Close();
            global::Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static double Luminance(global::Avalonia.Media.Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(global::Avalonia.Media.Color a, global::Avalonia.Media.Color b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }

    // A secondary button is a filled box with an edge; when the edge is nearly the colour of the panel, the eye takes the
    // fill for the whole button and the button looks smaller than a filled primary one of the same height beside it.
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheEdgeOfASecondaryButton_Should_StandOutFromThePanel_InEveryTheme(string variantName)
    {
        var theme = new InvokletTheme();
        var variant = (global::Avalonia.Styling.ThemeVariant)typeof(InvokletThemeVariants).GetField(variantName)!.GetValue(null)!;

        theme.TryGetResource("Invoklet.Button.Secondary.BorderBrush", variant, out var border);
        theme.TryGetResource("Invoklet.Panel.BackgroundBrush", variant, out var panel);

        var contrast = Contrast(Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(border).Color,
            Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(panel).Color);
        Assert.True(contrast >= 1.9, $"{variantName}: {contrast:0.00}");
    }

    public static TheoryData<string> Variants => new("InvokletMidnight", "InvokletWarmLight", "RiderDark", "RiderLight",
        "Vs2022Dark", "Vs2022Light", "VsCodeDark", "VsCodeLight");
}
