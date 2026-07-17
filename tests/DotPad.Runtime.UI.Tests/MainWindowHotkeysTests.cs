using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

// TODO.md #80: F4 (open Dependencies) and Ctrl+W (close active tab) used to be literal Key.F4 /
// (Key.W + Control) checks in MainWindow.OnWindowKeyDown — sitting right next to 7 other gestures
// that already went through KeyboardSettings via Match(e, kb.X). These prove the two converted
// branches actually read from KeyboardSettings now (not just "F4/Ctrl+W still happen to work") by
// rebinding each gesture to something else and confirming the OLD literal key stops working while
// the NEW bound one does.
public class MainWindowHotkeysTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, Mock<IWindowService> WindowService) CreateMainWindow(
        KeyboardSettings keyboard)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
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
        var windowService = new Mock<IWindowService>();
        windowService.Setup(w => w.ShowDependenciesAsync(It.IsAny<IObservable<DotPad.Format.RPad.RpadFrontMatter?>?>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        var editorTabs = new EditorTabsControl(
            theme.Object, registry.Object, prefs.Object, () => intelliSense.Object,
            new CSharpReferenceService(), buildCache.Object);

        var window = new MainWindow(prefs.Object, editorTabs, mainVm);
        window.Show();
        return (window, mainVm, windowService);
    }

    [AvaloniaFact]
    public void PressingTheDefaultF4Gesture_Should_OpenDependencies()
    {
        var (window, _, windowService) = CreateMainWindow(new KeyboardSettings());

        window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);

        windowService.Verify(w => w.ShowDependenciesAsync(It.IsAny<IObservable<DotPad.Format.RPad.RpadFrontMatter?>?>()), Times.Once);
    }

    [AvaloniaFact]
    public void PressingF4_Should_NotOpenDependencies_WhenTheGestureHasBeenRebound()
    {
        var (window, _, windowService) = CreateMainWindow(new KeyboardSettings { OpenDependencies = "Ctrl+Shift+X" });

        window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);

        windowService.Verify(w => w.ShowDependenciesAsync(It.IsAny<IObservable<DotPad.Format.RPad.RpadFrontMatter?>?>()), Times.Never);
    }

    [AvaloniaFact]
    public void PressingTheReboundGesture_Should_OpenDependencies()
    {
        var (window, _, windowService) = CreateMainWindow(new KeyboardSettings { OpenDependencies = "Ctrl+Shift+X" });

        window.KeyPress(Key.X, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.X, null);

        windowService.Verify(w => w.ShowDependenciesAsync(It.IsAny<IObservable<DotPad.Format.RPad.RpadFrontMatter?>?>()), Times.Once);
    }

    [AvaloniaFact]
    public void PressingTheDefaultCtrlW_Should_CloseTheActiveTab()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings());
        var tabCountBefore = vm.Tabs.Count;
        Assert.True(tabCountBefore > 0); // the initial blank tab EditorTabsControl.Attach creates

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);

        Assert.Equal(tabCountBefore - 1, vm.Tabs.Count);
    }

    [AvaloniaFact]
    public void PressingCtrlW_Should_NotCloseTheActiveTab_WhenTheGestureHasBeenRebound()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings { CloseTab = "Ctrl+Shift+Q" });
        var tabCountBefore = vm.Tabs.Count;

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);

        Assert.Equal(tabCountBefore, vm.Tabs.Count);
    }

    [AvaloniaFact]
    public void PressingTheReboundGesture_Should_CloseTheActiveTab()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings { CloseTab = "Ctrl+Shift+Q" });
        var tabCountBefore = vm.Tabs.Count;
        Assert.True(tabCountBefore > 0);

        window.KeyPress(Key.Q, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Q, null);

        Assert.Equal(tabCountBefore - 1, vm.Tabs.Count);
    }
}
