using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// The file tree's New File submenu lists the registered languages, by their own names and icons, in
// registration order, and creates a file in the one picked.
public class FileTreeViewNewFileMenuTests
{
    private static (Window Window, FileTreeView View) Create(params ILanguageHostFactory[] languages)
    {
        var registry = new LanguageHostRegistry();
        foreach (var language in languages) registry.Register(language);
        var view = new FileTreeView { DataContext = new FileTreeViewModel(new Mock<IPreferencesService>().Object, registry) };
        var window = new Window { Content = view };
        window.Show();
        return (window, view);
    }

    private static List<MenuItem> NewFileItems(FileTreeView view)
        => [.. view.FindControl<MenuItem>("NewFileMenu")!.Items.Cast<MenuItem>()];

    // The icons are the app's resources (App.axaml includes them; the test application doesn't), and
    // the view gets its data context before it is in a window, as from MainWindow's DataTemplate.
    [AvaloniaFact]
    public void TheNewFileMenu_Should_OfferTheRegisteredLanguages_AsBefore()
    {
        var icons = new ResourceInclude(new Uri("avares://Invoklet/")) { Source = new Uri("avares://Invoklet/Resources/Icons.axaml") };
        Application.Current!.Resources.MergedDictionaries.Add(icons);
        var (window, view) = Create(new CSharpLanguageHostFactory(NullLoggerFactory.Instance), new VbLanguageHostFactory(NullLoggerFactory.Instance));
        try
        {
            var items = NewFileItems(view);

            Assert.Equal(["C#", "Visual Basic"], items.Select(i => i.Header as string));
            Assert.All(items, i => Assert.IsType<PathIcon>(i.Icon));

            var created = new List<MdRunLanguage>();
            view.CreateFile += (_, e) => created.Add(e.Language);
            foreach (var item in items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal([MdRunLanguage.CSharp, MdRunLanguage.Vb], created);
        }
        finally
        {
            window.Close();
            Application.Current.Resources.MergedDictionaries.Remove(icons);
        }
    }

    [AvaloniaFact]
    public void TheNewFileMenu_Should_OfferOnlyWhatIsRegistered()
    {
        var (window, view) = Create(new VbLanguageHostFactory(NullLoggerFactory.Instance));
        try
        {
            Assert.Equal(["Visual Basic"], NewFileItems(view).Select(i => i.Header as string));
        }
        finally { window.Close(); }
    }
}
