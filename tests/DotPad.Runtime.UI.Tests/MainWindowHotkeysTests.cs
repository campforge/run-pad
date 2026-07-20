using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

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
        // Ctrl+W's close path asks this whenever the active tab is untitled+dirty (EditorTabsControl.
        // RequestCloseTabAsync) — an unstubbed loose mock defaults an unconfigured Task<enum> call to
        // Task.FromResult(default(TEnum)), and UnsavedChangesResult's own default (0) is Save, not
        // Cancel/DontSave. That silently steers RequestCloseTabAsync into its Save branch, which then
        // calls the equally-unstubbed ShowSaveFileDialogAsync (defaults to a null path = "dialog
        // cancelled"), so the tab never actually closes — exactly the shape of the CI-only failure
        // this stub fixes. Explicit here rather than relying on Moq's default either way.
        windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(UnsavedChangesResult.DontSave);

        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object, () => intelliSense.Object,
            new CSharpReferenceService(), buildCache.Object);

        var window = new MainWindow(prefs.Object, editorTabs, mainVm);
        window.Show();
        return (window, mainVm, windowService);
    }

    // EditorTabsControl.OnTabsCollectionChanged builds each tab's EditorsTabItemControl
    // asynchronously (CreateControlAsync's own Task.Run hop for the IntelliSense factory, even a
    // trivial mocked one, still needs a real thread-pool round trip back to the UI thread) — a test
    // that presses a key immediately after CreateMainWindow returns races that completion. Harmless
    // for gestures that only touch the ViewModel (F4/OpenDependencies), but CloseActiveTab needs
    // GetActiveControl() to find the actual control, so on a slow/loaded CI runner (observed on
    // Windows CI) the control can still not exist yet — the close silently no-ops instead of
    // asserting anything, which is why this surfaced as "tab didn't close" rather than a crash.
    // Lifted out of PressingCtrlW_OnADirtyUntitledTab_Should_AskBeforeClosing, which already had to
    // solve this same race for itself.
    private static async Task<EditorsTabItemControl?> WaitForActiveTabControlAsync(Window window)
    {
        for (var i = 0; i < 100; i++)
        {
            if (window.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().FirstOrDefault() is { } ctrl)
                return ctrl;
            await Task.Delay(20);
        }

        return null;
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
    public async Task PressingTheDefaultCtrlW_Should_CloseTheActiveTab()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings());
        Assert.NotNull(await WaitForActiveTabControlAsync(window));
        var tabCountBefore = vm.Tabs.Count;
        Assert.True(tabCountBefore > 0); // the initial blank tab EditorTabsControl.Attach creates

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);

        Assert.Equal(tabCountBefore - 1, vm.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task PressingCtrlW_Should_NotCloseTheActiveTab_WhenTheGestureHasBeenRebound()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings { CloseTab = "Ctrl+Shift+Q" });
        Assert.NotNull(await WaitForActiveTabControlAsync(window));
        var tabCountBefore = vm.Tabs.Count;

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);

        Assert.Equal(tabCountBefore, vm.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task PressingTheReboundGesture_Should_CloseTheActiveTab()
    {
        var (window, vm, _) = CreateMainWindow(new KeyboardSettings { CloseTab = "Ctrl+Shift+Q" });
        Assert.NotNull(await WaitForActiveTabControlAsync(window));
        var tabCountBefore = vm.Tabs.Count;
        Assert.True(tabCountBefore > 0);

        window.KeyPress(Key.Q, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Q, null);

        Assert.Equal(tabCountBefore - 1, vm.Tabs.Count);
    }

    // TODO.md, "Жизненный цикл вкладок (остаток)": Ctrl+W used to call MainWindowViewModel.
    // CloseActiveTab directly, bypassing whatever EditorTabsControl.RequestCloseTabAsync does —
    // confirms the hotkey path now goes through the same unsaved-changes confirmation as the tab's
    // own [x] button (see EditorTabsControlCloseConfirmationTests for the confirmation flow itself).
    [AvaloniaFact]
    public async Task PressingCtrlW_OnADirtyUntitledTab_Should_AskBeforeClosing()
    {
        var (window, vm, windowService) = CreateMainWindow(new KeyboardSettings());
        windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(UnsavedChangesResult.Cancel);

        var ctrl = await WaitForActiveTabControlAsync(window);
        Assert.NotNull(ctrl);
        ctrl!.State.Editor.Text = "Console.WriteLine(1);";
        var tabCountBefore = vm.Tabs.Count;

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
        await Task.Delay(50);

        windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Once);
        Assert.Equal(tabCountBefore, vm.Tabs.Count); // Cancel — still open
    }
}
