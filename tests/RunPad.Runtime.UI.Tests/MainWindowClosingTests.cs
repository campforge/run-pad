using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
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

// TODO.md, "Жизненный цикл вкладок (остаток)": quitting the app used to skip the unsaved-changes
// confirmation entirely (only closing an individual tab asked — see
// EditorTabsControlCloseConfirmationTests) — MainWindow.OnClosing now cancels the first Closing,
// awaits EditorTabsControl.ConfirmCloseAllUnsavedTabsAsync, and only actually closes once that
// resolves true. These drive a real MainWindow through Close() end to end rather than calling the
// confirmation methods directly.
public class MainWindowClosingTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, Mock<IWindowService> WindowService)
        CreateMainWindow()
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
        var windowService = new Mock<IWindowService>();

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

    private static async Task<EditorsTabItemControl> GetSoleControlAsync(MainWindow window)
    {
        for (var i = 0; i < 100; i++)
        {
            var ctrl = window.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().FirstOrDefault();
            if (ctrl is not null) return ctrl;
            await Task.Delay(20);
        }
        throw new TimeoutException("No EditorsTabItemControl was realized in time.");
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_WithNothingUnsaved_Should_CloseImmediately_WithoutAskingAnything()
    {
        var (window, _, windowService) = CreateMainWindow();
        await GetSoleControlAsync(window); // let the initial (pristine, untitled) tab realize first

        var closedFired = false;
        window.Closed += (_, _) => closedFired = true;

        window.Close();
        await Task.Delay(50);

        Assert.True(closedFired);
        windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_WithADirtyUntitledTab_AndCancelling_Should_KeepTheWindowOpen()
    {
        var (window, _, windowService) = CreateMainWindow();
        var ctrl = await GetSoleControlAsync(window);
        ctrl.State.Editor.Text = "Console.WriteLine(1);";
        windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(UnsavedChangesResult.Cancel);

        var closedFired = false;
        window.Closed += (_, _) => closedFired = true;

        window.Close();
        await Task.Delay(50);

        Assert.False(closedFired);
        windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_WithADirtyUntitledTab_AndChoosingDontSave_Should_ActuallyClose()
    {
        var (window, _, windowService) = CreateMainWindow();
        var ctrl = await GetSoleControlAsync(window);
        ctrl.State.Editor.Text = "Console.WriteLine(1);";
        windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(UnsavedChangesResult.DontSave);

        var closedFired = false;
        window.Closed += (_, _) => closedFired = true;

        window.Close();
        await Task.Delay(50);

        // The confirmed, second Close() has to make it all the way through the (now no-op) Closing
        // handler again — this is what actually proves the _confirmedClose re-entry guard works,
        // not just that ConfirmCloseAllUnsavedTabsAsync itself returns true in isolation.
        Assert.True(closedFired);
    }
}
