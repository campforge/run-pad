using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #42 — WindowDecorations.Full (the Linux fallback, since a CSD request comes back with no
// border/shadow/corner-radius on a real KDE/KWin/Wayland session) puts the OS's own native titlebar
// above our custom title row, which still drew its own copy of the app icon — a visible duplicate
// ("extra icon" from the original TODO note) since the native titlebar already shows Window.Icon.
public class MainWindowChromeTests
{
    private static MainWindow CreateMainWindow(IWindowService? windowService = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(Path.GetTempPath());
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
            windowService: windowService ?? new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object, () => intelliSense.Object,
            new CSharpReferenceService(), buildCache.Object);

        return new MainWindow(prefs.Object, editorTabs, mainVm);
    }

    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_HideNativeTitlebarDuplicates_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var window = CreateMainWindow();

        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.False(window.ExtendClientAreaToDecorationsHint);

        var windowControlButtons = window.FindControl<StackPanel>("WindowControlButtons");
        Assert.NotNull(windowControlButtons);
        Assert.False(windowControlButtons!.IsVisible);

        var appIcon = window.FindControl<Image>("AppIconImage");
        Assert.NotNull(appIcon);
        Assert.False(appIcon!.IsVisible);
    }

    // docs/handoffs/macos-native-header.md — stock macOS titlebar, menu moved to the system menu bar.
    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_UseNativeTitlebarWithoutCustomHeaderRow_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var window = CreateMainWindow();

        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.False(window.ExtendClientAreaToDecorationsHint);

        Assert.False(window.FindControl<Border>("TitleBar")!.IsVisible);
        Assert.Equal(0, window.FindControl<Grid>("RootLayout")!.RowDefinitions[0].Height.Value);
    }

    // docs/handoffs/menu-platform-parity.md — the same application menu paths on every OS. Runs on
    // any OS: both projections are built from MainWindow.MainMenuEntries regardless of platform.
    private static readonly string[] ExpectedMenuTree =
    [
        "File",
        "File/New File",
        "File/New RPad File",
        "File/New RPad File/C#",
        "File/New RPad File/Visual Basic",
        "File/Import LINQPad Query...",
        "File/Open Text File...",
        "File/---",
        "File/Save",
        "File/Save As...",
        "Edit",
        "Edit/Dependencies",
        "Edit/Assembly Explorer...",
        "Edit/---",
        "Edit/Preferences",
        "View",
        "View/Toggle File Tree",
        "Help",
        "Help/About RunPad",
    ];

    [AvaloniaFact]
    public void MainMenu_Should_ProjectIdenticalTrees_ForInWindowAndNativeMenus()
    {
        using var window = CreateMainWindow();

        var inWindow = Describe(window.FindControl<Menu>("MainMenu")!.Items, "").ToList();
        var native = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "").ToList();

        Assert.Equal(ExpectedMenuTree, inWindow.Select(n => n.Path));
        Assert.Equal(ExpectedMenuTree, native.Select(n => n.Path));
        // Same ICommand instance behind each path → identical routing, not just identical labels.
        Assert.Equal(inWindow.Select(n => n.Command), native.Select(n => n.Command));
        Assert.All(inWindow.Where(n => IsLeaf(n.Path)), n => Assert.NotNull(n.Command));

        // Only macOS exports a window NativeMenu (system menu bar); elsewhere it would duplicate the
        // in-window Menu (e.g. a Linux DBus global menu).
        var exported = NativeMenu.GetMenu(window);
        if (OperatingSystem.IsMacOS())
        {
            Assert.NotNull(exported);
            var exportedNodes = Describe(exported!, "").ToList();
            Assert.Equal(ExpectedMenuTree, exportedNodes.Select(n => n.Path));
            Assert.Equal(inWindow.Select(n => n.Command), exportedNodes.Select(n => n.Command));
        }
        else
        {
            Assert.Null(exported);
        }
    }

    [AvaloniaFact]
    public void MainMenu_Should_RouteToRealActions()
    {
        var windowService = new Mock<IWindowService>();
        using var window = CreateMainWindow(windowService.Object);
        var vm = (MainWindowViewModel)window.DataContext!;
        var commands = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "")
            .Where(n => n.Command is not null)
            .ToDictionary(n => n.Path, n => n.Command!);

        var tabCountBefore = vm.Tabs.Count;
        commands["File/New File"].Execute(null);
        Assert.Equal(tabCountBefore + 1, vm.Tabs.Count);

        var sizeBefore = vm.Sidebar.Size;
        commands["View/Toggle File Tree"].Execute(null);
        Assert.NotEqual(sizeBefore, vm.Sidebar.Size);

        commands["Edit/Preferences"].Execute(null);
        commands["Help/About RunPad"].Execute(null);
        windowService.Verify(w => w.ShowPreferencesAsync(), Times.Once);
        windowService.Verify(w => w.ShowAboutAsync(), Times.Once);
    }

    // The application menu must already exist when App.Initialize returns — Avalonia.Native's
    // exporter is created right after (AfterSetup) and otherwise installs "About Avalonia".
    [AvaloniaFact]
    public void AppInitialize_Should_SetApplicationMenu_WithoutAvaloniaAbout()
    {
        var app = new App();
        app.Initialize();

        var menu = NativeMenu.GetMenu(app);
        Assert.NotNull(menu);
        var headers = menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
        Assert.Equal(["About RunPad"], headers);
        Assert.DoesNotContain(headers, h => h!.Contains("Avalonia"));
    }

    [AvaloniaFact]
    public void ApplicationMenu_About_Should_ShowAboutRunPad()
    {
        var windowService = new Mock<IWindowService>();
        var menu = App.CreateApplicationMenu(() => windowService.Object);

        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "About RunPad").Command!.Execute(null);

        windowService.Verify(w => w.ShowAboutAsync(), Times.Once);
    }

    private static bool IsLeaf(string path)
        => !path.EndsWith("---", StringComparison.Ordinal)
           && !ExpectedMenuTree.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal));

    private sealed record MenuNode(string Path, ICommand? Command);

    private static IEnumerable<MenuNode> Describe(IEnumerable<object?> items, string prefix)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case Separator:
                    yield return new MenuNode(prefix + "---", null);
                    break;
                case MenuItem menuItem:
                    var path = prefix + MainMenuDefinition.StripAccessKey((string)menuItem.Header!);
                    yield return new MenuNode(path, menuItem.Command);
                    foreach (var child in Describe(menuItem.Items, path + "/")) yield return child;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected menu item {item}");
            }
        }
    }

    private static IEnumerable<MenuNode> Describe(NativeMenu menu, string prefix)
    {
        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case NativeMenuItemSeparator:
                    yield return new MenuNode(prefix + "---", null);
                    break;
                case NativeMenuItem nativeItem:
                    var path = prefix + nativeItem.Header;
                    yield return new MenuNode(path, nativeItem.Command);
                    if (nativeItem.Menu is { } sub)
                    {
                        foreach (var child in Describe(sub, path + "/")) yield return child;
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected native menu item {item}");
            }
        }
    }

    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_KeepClientSideDecorations_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var window = CreateMainWindow();

        var windowControlButtons = window.FindControl<StackPanel>("WindowControlButtons");
        Assert.NotNull(windowControlButtons);
        Assert.True(windowControlButtons!.IsVisible);

        var appIcon = window.FindControl<Image>("AppIconImage");
        Assert.NotNull(appIcon);
        Assert.True(appIcon!.IsVisible);
    }
}
