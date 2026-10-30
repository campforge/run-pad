using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Services.Preferences;

public class PreferencesEditorLayoutTests
{
    [AvaloniaTheory]
    [InlineData("VsCodeDark")]
    [InlineData("VsCodeLight")]
    public void GroupedEditorPage_Should_AlignFields_ScrollAndKeepBindings(string theme)
    {
        var settings = new AppSettings();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(settings.Editor);
        prefs.Setup(p => p.Appearance).Returns(settings.Appearance);
        prefs.Setup(p => p.CSharp).Returns(settings.CSharp);
        prefs.Setup(p => p.Keyboard).Returns(settings.Keyboard);
        prefs.Setup(p => p.Layout).Returns(settings.Layout);
        prefs.Setup(p => p.Session).Returns(settings.Session);
        prefs.Setup(p => p.Debugger).Returns(settings.Debugger);
        prefs.Setup(p => p.Experimental).Returns(settings.Experimental);
        var themes = new Mock<IThemeService>();
        themes.Setup(t => t.InterfaceThemes).Returns([]);
        themes.Setup(t => t.EditorThemes).Returns([]);
        themes.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new PreferencesWindowViewModel(themes.Object, prefs.Object, new LanguageHostRegistry());
        vm.FilterText = "Editor";
        vm.SelectedNode = vm.Nodes.Single(n => n.Id == "editor");
        var window = new PreferencesWindow(vm) { Width = 640, Height = 420 };
        window.Styles.Add(new InvokletTheme());
        window.RequestedThemeVariant = (ThemeVariant)typeof(InvokletThemeVariants).GetField(theme)!.GetValue(null)!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var page = window.FindControl<StackPanel>("EditorSettingsPage")!;
        Assert.True(page.IsEffectivelyVisible);
        var family = window.FindControl<TextBox>("EditorFontFamilyBox")!;
        var size = window.FindControl<NumericUpDown>("EditorFontSizeBox")!;
        var language = window.FindControl<ComboBox>("NewFileLanguageBox")!;
        var x = family.TranslatePoint(default, window)!.Value.X;
        Assert.Equal(x, size.TranslatePoint(default, window)!.Value.X);
        Assert.Equal(x, language.TranslatePoint(default, window)!.Value.X);
        Assert.True(family.TranslatePoint(default, window)!.Value.X + family.Bounds.Width <= window.ClientSize.Width);
        var scroll = page.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        language.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Offset.Y > 0);

        family.Focus(NavigationMethod.Tab);
        family.Text = "Menlo";
        size.Focus(NavigationMethod.Tab);
        size.Value = 18;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Menlo", settings.Editor.FontFamily);
        Assert.Equal(18, settings.Editor.FontSize);
        var wrap = page.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Wrap long lines in code editor");
        // Results wrapping moved to the output settings menu beside the results.
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Wrap long lines in results");
        Assert.True(settings.Editor.ResultsWordWrap);
        Assert.False(settings.Editor.WordWrap);
        wrap.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(settings.Editor.WordWrap);
        Assert.True(settings.Editor.ResultsWordWrap);
        prefs.Verify(p => p.Save(), Times.AtLeastOnce);
        window.Close();
    }
}
