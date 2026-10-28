using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RunPad.Avalonia.Themes;
using RunPad.Contracts.IntelliSense;
using RunPad.UserControls;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The popup menu style reaches menu items only; the shared dialog badge maps each kind to its glyph
// and theme brush; the breakpoint condition dialog cleans up after itself on every way out; and
// the borderless chrome is attached once per window.
public class PopupStylesAndDialogLifecycleTests
{
    private static readonly ThemeVariant[] Variants =
    [
        RunPadThemeVariants.RiderDark, RunPadThemeVariants.RiderLight,
        RunPadThemeVariants.RunPadMidnight, RunPadThemeVariants.RunPadWarmLight,
        RunPadThemeVariants.VsCodeDark, RunPadThemeVariants.VsCodeLight,
        RunPadThemeVariants.Vs2022Dark, RunPadThemeVariants.Vs2022Light,
    ];

    private static (RunPadTheme Theme, RunPad.Resources.PopupStyles Styles) Install()
    {
        var theme = new RunPadTheme();
        var styles = new RunPad.Resources.PopupStyles();
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(styles);
        return (theme, styles);
    }

    private static void Uninstall((RunPadTheme Theme, RunPad.Resources.PopupStyles Styles) installed)
    {
        Application.Current!.Styles.Remove(installed.Styles);
        Application.Current.Styles.Remove(installed.Theme);
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static BreakpointConditionEditor NewEditor()
        => new(null, null, true, (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(null));

    // ── Popup menu style ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PopupMenuStyle_Should_ReachMenuAndContextMenuItems_ButNotAMenuItemElsewhere()
    {
        var installed = Install();
        // A marker colour for the palette's text brush, so the popup style's value is unmistakable.
        var marker = new SolidColorBrush(Colors.Lime);
        var inline = new MenuItem { Header = "Inline" };
        var inMenu = new MenuItem { Header = "File" };
        var menu = new Menu();
        menu.Items.Add(inMenu);
        var inContext = new MenuItem { Header = "Copy" };
        var context = new ContextMenu();
        context.Items.Add(inContext);
        var target = new Border { Width = 100, Height = 20, ContextMenu = context };
        var host = new Window
        {
            Width = 600,
            Height = 300,
            RequestedThemeVariant = RunPadThemeVariants.RiderDark,
            Content = new StackPanel { Children = { menu, inline, target } },
        };
        host.Resources["RunPad.Text.PrimaryBrush"] = marker;
        try
        {
            host.Show();
            context.Open(target);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Colors.Lime, ColorOf(inMenu.Foreground));
            Assert.Equal(Colors.Lime, ColorOf(inContext.Foreground));
            Assert.NotEqual(Colors.Lime, ColorOf(inline.Foreground));
        }
        finally
        {
            context.Close();
            host.Close();
            Uninstall(installed);
        }
    }

    // A menu bar's dropdown and its submenus are each MenuItem's own popup, not a MenuFlyoutPresenter:
    // they take the palette's panel colours, look like a context menu, and a submenu opens at its
    // parent's edge, not over it. The main window's style for the bar's own items stops at the bar.
    [AvaloniaFact]
    public void MenuPopups_Should_LookLikeAContextMenu_AndNotOverlapTheParent()
    {
        var installed = Install();
        var file = new MenuItem { Header = "File" };
        var submenu = new MenuItem { Header = "New RPad File" };
        submenu.Items.Add(new MenuItem { Header = "C#" });
        file.Items.Add(submenu);
        var menu = new Menu();
        menu.Styles.Add(new Style(x => x.OfType<Menu>().Child().OfType<MenuItem>())
        {
            Setters = { new Setter(TemplatedControl.FontSizeProperty, 14.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(4)) },
        });
        menu.Items.Add(file);
        var copy = new MenuItem { Header = "Copy" };
        var context = new ContextMenu();
        context.Items.Add(copy);
        var target = new Border { Width = 100, Height = 20, ContextMenu = context };
        var host = new Window { Width = 600, Height = 300, Content = new StackPanel { Children = { menu, target } } };
        try
        {
            host.Show();
            file.IsSubMenuOpen = true;
            submenu.IsSubMenuOpen = true;
            context.Open(target);
            Dispatcher.UIThread.RunJobs();
            var popups = new[] { file, submenu }
                .Select(item => item.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "PART_Popup")).ToArray();
            Assert.Equal(0, popups[1].HorizontalOffset);
            Assert.Equal(14, file.FontSize);
            Assert.Equal(copy.FontSize, submenu.FontSize);
            Assert.Equal(copy.Padding, submenu.Padding);
            foreach (var popup in popups)
            {
                var chrome = Assert.IsType<Border>(popup.Child);
                Assert.Equal(context.CornerRadius, chrome.CornerRadius);
                Assert.Equal(context.Padding, chrome.Padding);
                Assert.Equal(context.BorderThickness, chrome.BorderThickness);
            }
            foreach (var variant in Variants)
            {
                host.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                installed.Theme.TryGetResource("RunPad.Panel.BackgroundBrush", variant, out var panel);
                installed.Theme.TryGetResource("RunPad.Border.BackgroundBrush", variant, out var border);
                foreach (var popup in popups)
                {
                    var chrome = Assert.IsType<Border>(popup.Child);
                    Assert.Equal(ColorOf((IBrush)panel!), ColorOf(chrome.Background));
                    Assert.Equal(ColorOf((IBrush)border!), ColorOf(chrome.BorderBrush));
                }
                Assert.Equal(ColorOf((IBrush)panel!), ColorOf(context.Background));
                Assert.Equal(ColorOf((IBrush)border!), ColorOf(context.BorderBrush));
            }
        }
        finally
        {
            context.Close();
            file.IsSubMenuOpen = false;
            host.Close();
            Uninstall(installed);
        }
    }

    // ── Dialog badge ─────────────────────────────────────────────────────────

    [AvaloniaTheory]
    [InlineData(DialogKind.Question, "?", "RunPad.Button.Primary.BackgroundBrush")]
    [InlineData(DialogKind.Warning, "!", "RunPad.Status.PausedBrush")]
    [InlineData(DialogKind.Error, "×", "RunPad.Status.ErrorBrush")]
    public void DialogLayout_Should_ShowEachKindsGlyph_InItsThemeBrush_ForEveryPalette(DialogKind kind, string glyph, string brushKey)
    {
        var installed = Install();
        var layout = new DialogLayout { Kind = kind, Heading = "Heading" };
        var host = new Window { Width = 500, Height = 300, Content = layout };
        try
        {
            host.Show();
            var text = layout.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "KindGlyph");
            var badge = layout.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "KindBadge");
            Assert.Equal(glyph, text.Text);
            foreach (var variant in Variants)
            {
                host.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                Assert.True(installed.Theme.TryGetResource(brushKey, variant, out var expected), $"{brushKey} in {variant}");
                Assert.Equal(ColorOf((IBrush)expected!), ColorOf(badge.Background));
            }
        }
        finally
        {
            host.Close();
            Uninstall(installed);
        }
    }

    [AvaloniaFact]
    public void DialogLayout_Should_FollowAKindChange()
    {
        var installed = Install();
        var layout = new DialogLayout();
        var host = new Window { Content = layout, RequestedThemeVariant = RunPadThemeVariants.RunPadMidnight };
        try
        {
            host.Show();
            var text = layout.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "KindGlyph");
            var badge = layout.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "KindBadge");
            Assert.Equal("?", text.Text);
            layout.Kind = DialogKind.Error;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("×", text.Text);
            installed.Theme.TryGetResource("RunPad.Status.ErrorBrush", RunPadThemeVariants.RunPadMidnight, out var error);
            Assert.Equal(ColorOf((IBrush)error!), ColorOf(badge.Background));
        }
        finally
        {
            host.Close();
            Uninstall(installed);
        }
    }

    // ── Breakpoint condition dialog ──────────────────────────────────────────

    [AvaloniaFact]
    public void BreakpointConditionDialog_Should_FollowTheOwnersTheme_UntilClosed_ThenReleaseEverything()
    {
        var installed = Install();
        var owner = new Window { RequestedThemeVariant = RunPadThemeVariants.RiderDark };
        var editor = NewEditor();
        try
        {
            owner.Show();
            var dialog = new BreakpointConditionDialog(editor, owner);
            Assert.Equal(owner.ActualThemeVariant, dialog.RequestedThemeVariant);
            dialog.Show(owner);

            owner.RequestedThemeVariant = RunPadThemeVariants.VsCodeLight;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(RunPadThemeVariants.VsCodeLight, dialog.RequestedThemeVariant);
            Assert.True(editor.HasFinishedHandlers);
            Assert.False(editor.IsDisposed);

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsDisposed);
            Assert.False(editor.HasFinishedHandlers);

            owner.RequestedThemeVariant = RunPadThemeVariants.RunPadWarmLight;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(RunPadThemeVariants.VsCodeLight, dialog.RequestedThemeVariant); // no longer listening
        }
        finally
        {
            owner.Close();
            Uninstall(installed);
        }
    }

    [AvaloniaFact]
    public void BreakpointConditionDialog_Should_ReleaseEverything_WhenClosedWithoutBeingShown()
    {
        // The tab closes the dialog in a finally block, also when ShowDialog never got to show it.
        var owner = new Window();
        var editor = NewEditor();
        try
        {
            owner.Show();
            var dialog = new BreakpointConditionDialog(editor, owner);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(editor.IsDisposed);
            Assert.False(editor.HasFinishedHandlers);
            var before = dialog.RequestedThemeVariant;
            owner.RequestedThemeVariant = RunPadThemeVariants.VsCodeLight;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, dialog.RequestedThemeVariant);
        }
        finally
        {
            owner.Close();
        }
    }

    // ── Borderless chrome ────────────────────────────────────────────────────

    [AvaloniaFact]
    public void BorderlessChrome_Should_AttachOncePerWindow()
    {
        var window = new GoToLineDialog(1, 10);
        try
        {
            Assert.True(BorderlessWindowChrome.IsAttached(window));
            var surface = window.FindControl<Border>("DialogSurface")!;
            Assert.Throws<InvalidOperationException>(() => BorderlessWindowChrome.Attach(window, surface));

            var plain = new Window { Content = new Border() };
            Assert.False(BorderlessWindowChrome.IsAttached(plain));
            BorderlessWindowChrome.Attach(plain, (Border)plain.Content!);
            Assert.True(BorderlessWindowChrome.IsAttached(plain));
            plain.Close();
        }
        finally
        {
            window.Close();
        }
    }
}
