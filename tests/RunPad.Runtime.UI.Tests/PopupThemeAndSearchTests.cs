using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RunPad.Avalonia.Themes;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class PopupThemeAndSearchTests
{
    [AvaloniaFact]
    public void OpenSurfaces_Should_FollowEveryInterfacePalette_WithoutRecreatingControls()
    {
        var app = Application.Current!;
        var theme = new RunPadTheme();
        var styles = new RunPad.Resources.PopupStyles();
        app.Styles.Add(theme);
        app.Styles.Add(styles);
        var editor = new TextEditor { Text = "one two one" };
        var flyout = new FlyoutPresenter { Content = new CheckBox { Content = "Break on exceptions" } };
        var menu = new MenuFlyoutPresenter();
        var host = new Window { Width = 900, Height = 500, Content = new StackPanel { Children = { flyout, menu, editor } } };
        try
        {
            host.Show();
            editor.SearchPanel.Open();
            host.UpdateLayout();
            Assert.Equal(6, editor.SearchPanel.Margin.Left);
            Assert.Equal(6, editor.SearchPanel.Margin.Right);
            var options = editor.SearchPanel.GetVisualDescendants().OfType<ToggleButton>()
                .Where(t => t.Classes.Contains("searchOption")).ToArray();
            Assert.Equal(3, options.Length);
            foreach (var option in options) option.IsChecked = true;
            Assert.True(editor.SearchPanel.MatchCase);
            Assert.True(editor.SearchPanel.WholeWords);
            Assert.True(editor.SearchPanel.UseRegex);
            foreach (var variant in new[] { RunPadThemeVariants.RunPadMidnight, RunPadThemeVariants.RunPadWarmLight,
                RunPadThemeVariants.RiderDark, RunPadThemeVariants.RiderLight, RunPadThemeVariants.VsCodeDark,
                RunPadThemeVariants.VsCodeLight, RunPadThemeVariants.Vs2022Dark, RunPadThemeVariants.Vs2022Light })
            {
                host.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                host.UpdateLayout();
                object? Get(string key) { theme.TryGetResource(key, variant, out var value); return value; }
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(Get("RunPad.Panel.BackgroundBrush"));
                var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(Get("RunPad.Text.PrimaryBrush"));
                Assert.Equal(background.Color, Assert.IsAssignableFrom<ISolidColorBrush>(flyout.Background).Color);
                Assert.Equal(background.Color, Assert.IsAssignableFrom<ISolidColorBrush>(menu.Background).Color);
                Assert.Equal(background.Color, Assert.IsAssignableFrom<ISolidColorBrush>(editor.SearchPanel.Background).Color);
                Assert.Equal(foreground.Color, Assert.IsAssignableFrom<ISolidColorBrush>(menu.Foreground).Color);
                var selected = Assert.IsAssignableFrom<ISolidColorBrush>(Get("RunPad.ToolTab.SelectedBackgroundBrush"));
                var accent = Assert.IsAssignableFrom<ISolidColorBrush>(Get("RunPad.Tab.AccentBrush"));
                foreach (var option in options)
                {
                    Assert.Equal(selected.Color, Assert.IsAssignableFrom<ISolidColorBrush>(option.Background).Color);
                    Assert.Equal(accent.Color, Assert.IsAssignableFrom<ISolidColorBrush>(option.BorderBrush).Color);
                    var outline = option.GetVisualDescendants().OfType<Border>().First();
                    Assert.Equal(accent.Color, Assert.IsAssignableFrom<ISolidColorBrush>(outline.BorderBrush).Color);
                }
            }
        }
        finally { host.Close(); app.Styles.Remove(styles); app.Styles.Remove(theme); }
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(420)]
    public void SearchStrip_Should_KeepNavigationReplaceAndCloseWorking(int width)
    {
        var editor = new TextEditor { Text = "one two one" };
        var host = new Window { Width = width, Height = 450, Content = editor };
        host.Show();
        try
        {
            var search = editor.SearchPanel;
            search.Open();
            host.UpdateLayout();
            Assert.True(search.Bounds.Width >= width - 40);
            search.SearchPattern = "one";
            search.FindNext(0);
            Assert.Equal("one", editor.SelectedText);
            search.FindNext();
            Assert.Equal(8, editor.SelectionStart);
            search.IsReplaceMode = true;
            host.UpdateLayout();
            var input = search.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_searchTextBox");
            var replace = search.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_replaceTextBox");
            Assert.True(replace.IsEffectivelyVisible);
            var close = search.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseSearchButton");
            Assert.True(close.TranslatePoint(default, host)!.Value.X >= input.TranslatePoint(default, host)!.Value.X + input.Bounds.Width);
            Assert.True(close.TranslatePoint(default, host)!.Value.X + close.Bounds.Width <= host.ClientSize.Width);
            Assert.Equal(input.Bounds.Width, replace.Bounds.Width, 1);
            Assert.True(input.Bounds.Width >= (width >= 700 ? 360 : 200));
            Assert.Equal(input.TranslatePoint(default, host)!.Value.X, replace.TranslatePoint(default, host)!.Value.X, 1);
            Assert.True(replace.TranslatePoint(default, host)!.Value.Y > input.TranslatePoint(default, host)!.Value.Y);
            search.ReplacePattern = "three";
            search.ReplaceAll();
            Assert.Equal("three two three", editor.Text);
            search.Close();
            Assert.True(search.IsClosed);
        }
        finally { host.Close(); }
    }
}
