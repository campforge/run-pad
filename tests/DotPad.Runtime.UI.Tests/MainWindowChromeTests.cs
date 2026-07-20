using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #42 — WindowDecorations.Full (the Linux fallback, since a CSD request comes back with no
// border/shadow/corner-radius on a real KDE/KWin/Wayland session) puts the OS's own native titlebar
// above our custom title row, which still drew its own copy of the app icon — a visible duplicate
// ("лишняя иконка" from the original TODO note) since the native titlebar already shows Window.Icon.
public class MainWindowChromeTests
{
    private static MainWindow CreateMainWindow()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(Path.GetTempPath());
        fileSystem.Setup(f => f.GetFilesTree()).Returns([]);

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

    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_OnlyHideWindowControlButtons_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var window = CreateMainWindow();

        var windowControlButtons = window.FindControl<StackPanel>("WindowControlButtons");
        Assert.NotNull(windowControlButtons);
        Assert.False(windowControlButtons!.IsVisible);

        // Native traffic lights render over the extended client area on macOS — our own app icon
        // doesn't collide with a native titlebar the way it does on Linux, so it stays visible.
        var appIcon = window.FindControl<Image>("AppIconImage");
        Assert.NotNull(appIcon);
        Assert.True(appIcon!.IsVisible);
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
