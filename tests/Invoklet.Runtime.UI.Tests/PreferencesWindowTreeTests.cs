using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using TextMateSharp.Grammars;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The selected node's text color used to leak into its children: the style matched every TextBlock
// below a selected item, so with ".NET" selected, "C#" and "Visual Basic" turned white on the
// light background.
public class PreferencesWindowTreeTests
{
    private static readonly IBrush Primary = Brushes.Black;
    private static readonly IBrush Accent = Brushes.White;

    [AvaloniaFact]
    public void SelectingAParentNode_Should_ColorOnlyItsOwnText()
    {
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Light, false),
            new EditorTheme("Test", ThemeName.LightPlus, false)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Experimental).Returns(new ExperimentalSettings());
        var vm = new PreferencesWindowViewModel(theme.Object, prefs.Object, registry);

        var window = new PreferencesWindow(vm);
        window.Resources["Invoklet.Text.PrimaryBrush"] = Primary;
        window.Resources["Invoklet.Accent.ForegroundBrush"] = Accent;
        window.Resources["Invoklet.Accent.BackgroundBrush"] = Brushes.Blue;
        window.Show();
        try
        {
            foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>().ToList())
                item.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>().ToList())
                item.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            vm.SelectedNode = vm.Nodes.Single(n => n.Id == "languages").Children.Single(n => n.Id == "dotnet");
            Dispatcher.UIThread.RunJobs();

            Assert.Same(Accent, TextOf(window, ".NET").Foreground);
            Assert.Same(Primary, TextOf(window, "C#").Foreground);
            Assert.Same(Primary, TextOf(window, "Visual Basic").Foreground);
            Assert.Same(Primary, TextOf(window, "Keyboard").Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    private static TextBlock TextOf(Window window, string text)
        => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
}
