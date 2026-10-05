using Avalonia;
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

    [AvaloniaFact]
    public void NativeMenu_Should_ExposeFileEditViewMenus_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var window = CreateMainWindow();

        var menu = NativeMenu.GetMenu(window);
        Assert.NotNull(menu);
        Assert.Equal(["File", "Edit", "View"], menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header));

        string?[] expected =
        [
            "New File", "C#", "Visual Basic", "Import LINQPad Query...", "Save", "Save As...",
            "Dependencies", "Assembly Explorer...", "Toggle File Tree",
        ];
        var leaves = Leaves(menu).ToList();
        Assert.Equal(expected, leaves.Select(i => i.Header));
        Assert.All(leaves, i => Assert.NotNull(i.Command));
    }

    [AvaloniaFact]
    public void NativeMenu_Should_RouteToRealActions_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var windowService = new Mock<IWindowService>();
        using var window = CreateMainWindow(windowService.Object);
        var vm = (MainWindowViewModel)window.DataContext!;
        var leaves = Leaves(NativeMenu.GetMenu(window)!).ToDictionary(i => i.Header!);

        var tabCountBefore = vm.Tabs.Count;
        leaves["New File"].Command!.Execute(null);
        Assert.Equal(tabCountBefore + 1, vm.Tabs.Count);

        var sizeBefore = vm.Sidebar.Size;
        leaves["Toggle File Tree"].Command!.Execute(null);
        Assert.NotEqual(sizeBefore, vm.Sidebar.Size);

        // Application ("RunPad") menu — About/Preferences replace the in-window Help/Edit entries.
        var appLeaves = Leaves(NativeMenu.GetMenu(Application.Current!)!).ToDictionary(i => i.Header!);
        appLeaves["About RunPad"].Command!.Execute(null);
        appLeaves["Preferences..."].Command!.Execute(null);
        windowService.Verify(w => w.ShowAboutAsync(), Times.Once);
        windowService.Verify(w => w.ShowPreferencesAsync(), Times.Once);
    }

    private static IEnumerable<NativeMenuItem> Leaves(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator))
        {
            if (item.Menu is { } sub)
            {
                foreach (var leaf in Leaves(sub)) yield return leaf;
            }
            else
            {
                yield return item;
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
