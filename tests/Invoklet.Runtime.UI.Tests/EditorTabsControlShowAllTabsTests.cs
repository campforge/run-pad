using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The tab strip already scrolls
// once there are too many tabs to fit, but a user still has to scroll blindly to find a specific
// far-away one — this dropdown (a chevron button next to "+") lists every open tab by name so they
// can jump straight to it, the other half of what the user originally asked for
// ("...maybe add a scroll/buttons [<][>] or a dropdown list showing all tabs").
// EditorTabsControl.BuildAllTabsMenu is internal specifically so these can inspect the built
// ContextMenu's items directly — headless Avalonia has no reliable way to drive an actual popup
// open/click interaction, but the menu it would show is real, not a stub.
public class EditorTabsControlShowAllTabsTests
{
    private static (EditorTabsControl EditorTabs, MainWindowViewModel MainVm, string RootFolder) CreateHarness()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-show-all-tabs-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();
        var intelliSense = new Mock<IIntelliSenseService>();

        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
            buildCache.Object);

        var host = new Window { Content = editorTabs };
        host.Show();
        editorTabs.Attach(mainVm); // opens the initial blank tab

        return (editorTabs, mainVm, rootFolder);
    }

    [AvaloniaFact]
    public async Task BuildAllTabsMenu_Should_ListEveryOpenTab_ByFileName()
    {
        var (editorTabs, mainVm, rootFolder) = CreateHarness();
        try
        {
            var pathA = Path.Combine(rootFolder, "A.rpad");
            File.WriteAllText(pathA, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "A"));
            mainVm.OpenTab(pathA);
            await TestWait.WhileAsync(() => mainVm.Tabs.Count < 2);

            var menu = editorTabs.BuildAllTabsMenu();

            Assert.NotNull(menu);
            var names = menu!.Items.OfType<MenuItem>()
                .Select(i => ((TextBlock)i.Header!).Text).ToList();
            Assert.Equal(mainVm.Tabs.Select(t => t.FileName), names);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public void BuildAllTabsMenu_Should_BoldOnlyTheActiveTabsEntry()
    {
        var (editorTabs, mainVm, rootFolder) = CreateHarness();
        try
        {
            var tab = Assert.Single(mainVm.Tabs);
            Assert.Same(tab, mainVm.ActiveTab);

            var menu = editorTabs.BuildAllTabsMenu();

            var header = (TextBlock)Assert.Single(menu!.Items.OfType<MenuItem>()).Header!;
            Assert.Equal(FontWeight.Bold, header.FontWeight);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task BuildAllTabsMenu_Should_ActivateTheClickedTab()
    {
        var (editorTabs, mainVm, rootFolder) = CreateHarness();
        try
        {
            var firstTab = Assert.Single(mainVm.Tabs); // the initial blank tab

            // NewMdRunFile (unlike OpenTab) never auto-closes an existing blank tab — this is the one
            // reliable way to end up with two simultaneous tabs to click between.
            mainVm.NewMdRunFile();
            await TestWait.WhileAsync(() => mainVm.Tabs.Count < 2);
            Assert.NotSame(firstTab, mainVm.ActiveTab); // the new tab is active now, not the first one

            var menu = editorTabs.BuildAllTabsMenu();
            var firstItem = (MenuItem)menu!.Items[0]!;

            firstItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Same(firstTab, mainVm.ActiveTab);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public void BuildAllTabsMenu_Should_ColorADirtyTabsEntry()
    {
        var (editorTabs, mainVm, rootFolder) = CreateHarness();
        try
        {
            var tab = Assert.Single(mainVm.Tabs);
            tab.IsDirty = true;

            var menu = editorTabs.BuildAllTabsMenu();

            var header = (TextBlock)Assert.Single(menu!.Items.OfType<MenuItem>()).Header!;
            Assert.NotNull(header.Foreground);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
