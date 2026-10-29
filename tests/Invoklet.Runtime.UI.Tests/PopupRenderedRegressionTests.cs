using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Invoklet.Avalonia.Themes;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Open actual flyouts and verify their rendered text and responsive search controls.
public class PopupRenderedRegressionTests
{
    private static (InvokletTheme Theme, Invoklet.Resources.PopupStyles Styles) Install()
    {
        var theme = new InvokletTheme();
        var styles = new Invoklet.Resources.PopupStyles();
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(styles);
        return (theme, styles);
    }

    private static void Uninstall((InvokletTheme Theme, Invoklet.Resources.PopupStyles Styles) installed)
    {
        Application.Current!.Styles.Remove(installed.Styles);
        Application.Current.Styles.Remove(installed.Theme);
    }

    private static Color Brush(InvokletTheme theme, string key, ThemeVariant variant)
    {
        theme.TryGetResource(key, variant, out var value);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    // Flyouts that keep the presenter they create, to inspect it once really shown in a popup.
    private sealed class ProbeMenuFlyout : MenuFlyout
    {
        public Control? Presenter { get; private set; }
        protected override Control CreatePresenter() => Presenter = base.CreatePresenter();
    }

    private sealed class ProbeFlyout : Flyout
    {
        public Control? Presenter { get; private set; }
        protected override Control CreatePresenter() => Presenter = base.CreatePresenter();
    }

    // The presenter's popup root: it is really shown, not just created.
    private static Visual Shown(Control? presenter)
    {
        Assert.NotNull(presenter);
        var root = TopLevel.GetTopLevel(presenter);
        Assert.NotNull(root);
        // The headless platform shows popups in the window's overlay layer: no native popup root here.
        return root;
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    // The code-fix menu: a MenuFlyout opened from the editor, whose monospace font must not leak in.
    [AvaloniaTheory]
    [InlineData("InvokletMidnight")]
    [InlineData("InvokletWarmLight")]
    [InlineData("RiderLight")]
    public void ARealMenuFlyout_FromAMonospaceTarget_Should_UseThePalette_TheInterfaceFont_AndItsStates(string variantName)
    {
        var variant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(variantName)!.GetValue(null)!;
        var installed = Install();
        var target = new ContentControl { Width = 200, Height = 40, FontFamily = new FontFamily("Courier New"), FontSize = 22 };
        var host = new Window { Width = 600, Height = 400, Content = target, RequestedThemeVariant = variant };
        var flyout = new ProbeMenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "Add braces" },
                new MenuItem { Header = "Disabled action", IsEnabled = false },
            },
        };
        try
        {
            host.Show();
            flyout.ShowAt(target);
            Dispatcher.UIThread.RunJobs();
            var popup = Shown(flyout.Presenter);
            var presenter = popup.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            var items = presenter.GetVisualDescendants().OfType<MenuItem>().ToList();
            var headers = items.Select(i => i.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is { Length: > 0 })).ToList();

            var report = new List<string>();
            void Check(bool ok, string what) { if (!ok) report.Add(what); }
            Check(Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", variant) == ColorOf(presenter.Background),
                $"menu background {ColorOf(presenter.Background)} vs palette {Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", variant)}");
            Check(Brush(installed.Theme, "Invoklet.Text.PrimaryBrush", variant) == ColorOf(headers[0].Foreground),
                $"item text {ColorOf(headers[0].Foreground)} vs palette text {Brush(installed.Theme, "Invoklet.Text.PrimaryBrush", variant)}");
            Check(headers.All(h => h.FontFamily.Name != "Courier New"), $"item font {headers[0].FontFamily.Name} (the target's)");
            Check(headers.All(h => h.FontSize != 22), $"item font size {headers[0].FontSize} (the target's)");
            // A disabled action stays distinguishable from an enabled one.
            Check(ColorOf(headers[0].Foreground) != ColorOf(headers[1].Foreground),
                $"disabled text {ColorOf(headers[1].Foreground)} same as enabled");

            presenter.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            var root = items[0].GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            Check(Brush(installed.Theme, "Invoklet.Toolbar.Button.HoverBrush", variant) == ColorOf(root.Background),
                $"selected item {ColorOf(root.Background)} vs hover {Brush(installed.Theme, "Invoklet.Toolbar.Button.HoverBrush", variant)}");
            Assert.True(report.Count == 0, string.Join("; ", report));
        }
        finally { flyout.Hide(); host.Close(); Uninstall(installed); }
    }

    // A palette switch while the menu is open recolours it in place.
    [AvaloniaFact]
    public void AnOpenMenuFlyout_Should_FollowALivePaletteSwitch()
    {
        var installed = Install();
        var target = new ContentControl { Width = 200, Height = 40 };
        var host = new Window { Width = 600, Height = 400, Content = target, RequestedThemeVariant = InvokletThemeVariants.InvokletWarmLight };
        var flyout = new ProbeMenuFlyout { Items = { new MenuItem { Header = "Add braces" } } };
        try
        {
            host.Show();
            flyout.ShowAt(target);
            Dispatcher.UIThread.RunJobs();
            var presenter = Shown(flyout.Presenter).GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            Assert.Equal(Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", InvokletThemeVariants.InvokletWarmLight), ColorOf(presenter.Background));

            host.RequestedThemeVariant = InvokletThemeVariants.InvokletMidnight;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", InvokletThemeVariants.InvokletMidnight), ColorOf(presenter.Background));
        }
        finally { flyout.Hide(); host.Close(); Uninstall(installed); }
    }

    [AvaloniaTheory]
    [InlineData("InvokletMidnight")]
    [InlineData("InvokletWarmLight")]
    public void HelpMenu_Should_UseTheActivePaletteForPopupBorderAndSeparator(string variantName)
    {
        var variant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(variantName)!.GetValue(null)!;
        var installed = Install();
        var target = new ContentControl { Width = 200, Height = 40 };
        var host = new Window { Width = 600, Height = 400, Content = target, RequestedThemeVariant = variant };
        var flyout = new ProbeMenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "User Guide" },
                new Separator(),
                new MenuItem { Header = "About Invoklet" },
            },
        };
        try
        {
            host.Show();
            flyout.ShowAt(target);
            Dispatcher.UIThread.RunJobs();
            var popup = Shown(flyout.Presenter);
            var presenter = popup.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            var separator = popup.GetVisualDescendants().OfType<Separator>().Single();
            var border = presenter.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LayoutRoot");

            Assert.Equal(Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", variant), ColorOf(presenter.Background));
            Assert.Equal(Brush(installed.Theme, "Invoklet.Border.BackgroundBrush", variant), ColorOf(presenter.BorderBrush));
            Assert.Equal(Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", variant), ColorOf(border.Background));
            Assert.Equal(Brush(installed.Theme, "Invoklet.Border.BackgroundBrush", variant), ColorOf(border.BorderBrush));
            Assert.Equal(new Thickness(1), border.BorderThickness);
            Assert.Equal(Brush(installed.Theme, "Invoklet.Separator.BackgroundBrush", variant), ColorOf(separator.Background));
        }
        finally { flyout.Hide(); host.Close(); Uninstall(installed); }
    }

    // The exception-settings flyout: check boxes in a Flyout opened from a toolbar button.
    [AvaloniaFact]
    public void ARealFlyout_WithCheckBoxes_Should_UseThePalette_AndTheInterfaceFont()
    {
        var installed = Install();
        var variant = InvokletThemeVariants.InvokletMidnight;
        var target = new ContentControl { Width = 200, Height = 40, FontFamily = new FontFamily("Courier New") };
        var host = new Window { Width = 600, Height = 400, Content = target, RequestedThemeVariant = variant };
        var flyout = new ProbeFlyout { Content = new StackPanel { Children = { new CheckBox { Content = "Break on uncaught exceptions" } } } };
        try
        {
            host.Show();
            flyout.ShowAt(target);
            Dispatcher.UIThread.RunJobs();
            var popup = Shown(flyout.Presenter);
            var presenter = popup.GetVisualDescendants().OfType<FlyoutPresenter>().Single();
            var text = popup.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Break on uncaught exceptions");

            var report = new List<string>();
            if (Brush(installed.Theme, "Invoklet.Panel.BackgroundBrush", variant) != ColorOf(presenter.Background))
                report.Add($"flyout background {ColorOf(presenter.Background)}");
            if (Brush(installed.Theme, "Invoklet.Text.PrimaryBrush", variant) != ColorOf(text.Foreground))
                report.Add($"check box text {ColorOf(text.Foreground)} vs palette text {Brush(installed.Theme, "Invoklet.Text.PrimaryBrush", variant)}");
            if (text.FontFamily.Name == "Courier New") report.Add("check box font is the target's");
            Assert.True(report.Count == 0, string.Join("; ", report));
        }
        finally { flyout.Hide(); host.Close(); Uninstall(installed); }
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(320)]
    public void MatchFeedback_Should_RemainVisibleInBothLayouts(int width)
    {
        var editor = new TextEditor { Text = "one two one" };
        var host = new Window { Width = width, Height = 450, Content = editor };
        host.Show();
        try
        {
            editor.SearchPanel.Open();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            editor.SearchPanel.SearchPattern = "absent";
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var message = editor.SearchPanel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_MessageContent");
            Assert.True(message.IsEffectivelyVisible);
            Assert.False(string.IsNullOrWhiteSpace(message.Text));
            Assert.True(message.TranslatePoint(new Point(message.Bounds.Width, 0), editor.SearchPanel)!.Value.X
                <= editor.SearchPanel.Bounds.Width + 0.5);
        }
        finally { host.Close(); }
    }

    // Every control of the strip lies inside the strip (nothing pushed past its right edge), with
    // the "No matches found" message and the replacement row shown, and the input keeps room to type.
    [AvaloniaTheory]
    [InlineData(420, 1.0)]
    [InlineData(320, 1.0)]
    [InlineData(260, 1.0)]
    [InlineData(420, 1.5)]
    [InlineData(420, 2.0)]
    public void TheSearchStrip_Should_FitItsControls(int width, double scale)
    {
        var editor = new TextEditor { Text = "one two one" };
        var host = new Window
        {
            Width = width,
            Height = 450,
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = editor },
        };
        host.Show();
        try
        {
            var search = editor.SearchPanel;
            search.Open();
            search.IsReplaceMode = true;
            search.SearchPattern = "nothing-like-this";
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();

            var panelWidth = search.Bounds.Width;
            var controls = search.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Button or ToggleButton or TextBox && c.IsEffectivelyVisible).ToList();
            var outside = controls
                .Select(c => (c, right: c.TranslatePoint(new Point(c.Bounds.Width, 0), search)!.Value.X))
                .Where(x => x.right > panelWidth + 0.5)
                .Select(x => $"{x.c.GetType().Name} {x.c.Name ?? (x.c as ContentControl)?.Content?.ToString()} ends at {x.right:F0} > {panelWidth:F0}")
                .ToList();
            var input = controls.OfType<TextBox>().Single(t => t.Name == "PART_searchTextBox");
            var toggles = input.GetVisualDescendants().OfType<ToggleButton>().Sum(t => t.Bounds.Width);

            var room = input.Bounds.Width - toggles;
            Assert.True(outside.Count == 0 && room >= 40,
                $"panel {panelWidth:F0}px; typing room {room:F0}px of {input.Bounds.Width:F0}; outside: {string.Join("; ", outside)}");
        }
        finally { host.Close(); }
    }

}
