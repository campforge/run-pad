using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Closing an untitled tab with real typed content used
// to discard it silently — SaveIfNeeded is a no-op without a FilePath, so nothing about closing an
// untitled tab ever asked first. MainWindowViewModel's IEditorTabActions.CloseAsync (the real body
// behind EditorTabItemViewModel.CloseCommand — wired to both the tab's [x] button
// and Ctrl+W, see EditorTabsControl.CloseActiveTab) now prompts Save/Don't Save/Cancel for that one
// specific case. These exercise the real close path end-to-end (a genuine EditorTabsControl +
// EditorsTabItemControl + CodeTextEditor, not just the ViewModel-level flags) against a mocked
// IWindowService, so the dialog's answer is scripted but everything downstream of it is real.
public class EditorTabsControlCloseConfirmationTests
{
    private sealed record Harness(
        Window Host, EditorTabsControl EditorTabs, MainWindowViewModel MainVm, Mock<IWindowService> WindowService,
        string RootFolder);

    private static Harness CreateHarness()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-close-confirm-test").FullName;

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

        var host = new Window { Content = editorTabs };
        host.Show();
        editorTabs.Attach(mainVm); // opens the initial blank (untitled, not-yet-dirty) tab

        return new Harness(host, editorTabs, mainVm, windowService, rootFolder);
    }

    // Same teardown as DocumentSafetyHeadlessTests.Cleanup, after the assertions: dirty flags are
    // dropped so teardown never asks or writes, the plain host Window is closed, and — since it has no
    // Dispose of its own — the EditorTabsControl (and with it the tab controls) and the view model are
    // disposed explicitly.
    private static void Cleanup(Harness h)
    {
        try
        {
            foreach (var tab in h.MainVm.Tabs)
                tab.IsDirty = false;
            h.Host.Close();
            Assert.False(h.Host.IsVisible);
        }
        finally
        {
            h.EditorTabs.Dispose();
            h.MainVm.Dispose();
        }

        foreach (var file in Directory.GetFiles(h.RootFolder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(h.RootFolder, recursive: true);
    }

    private static async Task<EditorsTabItemControl> GetSoleControlAsync(EditorTabsControl editorTabs)
    {
        for (var i = 0; i < 100; i++)
        {
            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().FirstOrDefault();
            if (ctrl is not null) return ctrl;
            await Task.Delay(20);
        }
        throw new TimeoutException("No EditorsTabItemControl was realized in time.");
    }

    [AvaloniaFact]
    public async Task ClosingAPristineUntitledTab_Should_NotShowADialog()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            var tab = Assert.Single(mainVm.Tabs);
            Assert.False(tab.IsDirty); // never edited — nothing worth asking about

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.Empty(mainVm.Tabs);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingCancel_Should_KeepTheTabOpen()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            var tab = Assert.Single(mainVm.Tabs);
            Assert.True(tab.IsDirty);

            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            Assert.Single(mainVm.Tabs); // still there
            Assert.True(tab.IsDirty); // untouched
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingDontSave_Should_DiscardAndClose()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            var tab = Assert.Single(mainVm.Tabs);

            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.DontSave);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            Assert.Empty(mainVm.Tabs);
            Assert.Empty(Directory.GetFiles(rootFolder)); // nothing was ever written
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingSave_Should_WriteItThenClose()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            var tab = Assert.Single(mainVm.Tabs);

            var chosenPath = Path.Combine(rootFolder, "Saved.rpad");
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Save);
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), rootFolder))
                .ReturnsAsync(chosenPath);

            tab.CloseCommand.Execute().Subscribe();
            await TestWait.WhileAsync(() => !File.Exists(chosenPath));

            Assert.True(File.Exists(chosenPath));
            Assert.Contains("Console.WriteLine(1);", File.ReadAllText(chosenPath));
            Assert.Empty(mainVm.Tabs);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndCancellingTheSaveAsDialog_Should_KeepTheTabOpen()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            var tab = Assert.Single(mainVm.Tabs);

            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Save);
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), rootFolder))
                .ReturnsAsync((string?)null); // user picked Save, then backed out of the file picker

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            // "Save" was chosen but never actually completed — closing anyway would silently lose
            // the content the user just said they wanted to keep.
            Assert.Single(mainVm.Tabs);
            Assert.Null(tab.FilePath);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyTabWithARealPath_Should_NotShowADialog()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var filePath = Path.Combine(rootFolder, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);
            var tab = mainVm.Tabs.Single(t => t.FilePath == filePath);

            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .Single(c => ReferenceEquals(c.DataContext, tab));
            ctrl.State.Editor.Text = "Console.WriteLine(2);";
            Assert.True(tab.IsDirty);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            // A dirty tab with a real path is written in place before it's removed (review R1) —
            // nothing to confirm while that write succeeds.
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.Empty(mainVm.Tabs);
            Assert.Contains("Console.WriteLine(2);", File.ReadAllText(filePath));
        }
        finally { Cleanup(h); }
    }

    // ── App-exit confirmation (MainWindow.Closing → HasUnsavedTabs/ConfirmCloseAllUnsavedTabsAsync) ──

    [AvaloniaFact]
    public async Task HasUnsavedTabs_Should_BeFalse_ForAPristineUntitledTab()
    {
        var h = CreateHarness();
        var (_, editorTabs, _, _, rootFolder) = h;
        try
        {
            await GetSoleControlAsync(editorTabs);
            Assert.False(editorTabs.HasUnsavedTabs());
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task HasUnsavedTabs_Should_BeTrue_OnceTheUntitledTabIsEdited()
    {
        var h = CreateHarness();
        var (_, editorTabs, _, _, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";

            Assert.True(editorTabs.HasUnsavedTabs());
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnTrue_AndAskNothing_WhenNothingIsDirty()
    {
        var h = CreateHarness();
        var (_, editorTabs, _, windowService, rootFolder) = h;
        try
        {
            await GetSoleControlAsync(editorTabs);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnFalse_WhenUserCancels()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.False(canClose);
            Assert.Single(mainVm.Tabs); // untouched — the tab itself isn't closed by this method either
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnTrue_WhenUserChoosesDontSave()
    {
        var h = CreateHarness();
        var (_, editorTabs, _, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.DontSave);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            Assert.Empty(Directory.GetFiles(rootFolder)); // discarded, nothing written
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_SaveIt_WhenUserChoosesSave()
    {
        var h = CreateHarness();
        var (_, editorTabs, _, windowService, rootFolder) = h;
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            var chosenPath = Path.Combine(rootFolder, "Saved.rpad");
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Save);
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), rootFolder))
                .ReturnsAsync(chosenPath);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            Assert.True(File.Exists(chosenPath));
            Assert.Contains("Console.WriteLine(1);", File.ReadAllText(chosenPath));
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_AskAboutEveryDirtyUntitledTab_NotJustTheFirst()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var firstCtrl = await GetSoleControlAsync(editorTabs);
            firstCtrl.State.Editor.Text = "Console.WriteLine(1);";

            mainVm.NewRpadFile();
            EditorsTabItemControl? secondCtrl = null;
            for (var i = 0; i < 100 && secondCtrl is null; i++)
            {
                secondCtrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                    .Select(t => t.Content).OfType<EditorsTabItemControl>()
                    .FirstOrDefault(c => !ReferenceEquals(c, firstCtrl));
                if (secondCtrl is null) await Task.Delay(20);
            }
            Assert.NotNull(secondCtrl);
            secondCtrl!.State.Editor.Text = "Console.WriteLine(2);";

            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.DontSave);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Exactly(2));
        }
        finally { Cleanup(h); }
    }

    // ── Review R1: named tabs whose write fails ─────────────────────────────────

    private static async Task<(EditorTabItemViewModel Tab, EditorsTabItemControl Control)> OpenNamedTabAsync(
        EditorTabsControl editorTabs, MainWindowViewModel mainVm, string filePath)
    {
        mainVm.OpenTab(filePath);
        var tab = mainVm.Tabs.Single(t => t.FilePath == filePath);
        for (var i = 0; i < 100; i++)
        {
            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .FirstOrDefault(c => ReferenceEquals(c.DataContext, tab));
            if (ctrl is not null && tab.FrontMatter is not null) return (tab, ctrl);
            await Task.Delay(20);
        }
        throw new TimeoutException("The named tab was not realized in time.");
    }

    [AvaloniaFact]
    public async Task ClosingADirtyNamedTab_WhoseWriteFails_AndCancelling_Should_KeepTheSameTabAndText()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        var filePath = Path.Combine(rootFolder, "demo.rpad");
        try
        {
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            var (tab, ctrl) = await OpenNamedTabAsync(editorTabs, mainVm, filePath);
            ctrl.State.Editor.Text = "Console.WriteLine(2);";
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            Assert.Contains(tab, mainVm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.Same(ctrl, editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .Single(c => ReferenceEquals(c.DataContext, tab)));
            Assert.Equal("Console.WriteLine(2);", ctrl.State.Editor.Text);
            windowService.Verify(w => w.ShowErrorAsync("Save Failed", It.IsAny<string>()), Times.Once);
        }
        finally
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Cleanup(h);
        }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_WriteAnInactiveDirtyNamedTab_WithoutAsking()
    {
        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        try
        {
            var filePath = Path.Combine(rootFolder, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            var (tab, ctrl) = await OpenNamedTabAsync(editorTabs, mainVm, filePath);
            ctrl.State.Editor.Text = "Console.WriteLine(3);";
            mainVm.NewRpadFile(); // makes the named tab inactive
            Assert.NotSame(tab, mainVm.ActiveTab);
            Assert.True(editorTabs.HasUnsavedTabs());

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            Assert.False(tab.IsDirty);
            Assert.Contains("Console.WriteLine(3);", File.ReadAllText(filePath));
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_CancelQuit_WhenAnInactiveNamedTabsWriteFails_AndUserCancels()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var h = CreateHarness();
        var (_, editorTabs, mainVm, windowService, rootFolder) = h;
        var filePath = Path.Combine(rootFolder, "demo.rpad");
        try
        {
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            var (tab, ctrl) = await OpenNamedTabAsync(editorTabs, mainVm, filePath);
            ctrl.State.Editor.Text = "Console.WriteLine(4);";
            mainVm.NewRpadFile();
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead);
            windowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.False(canClose);
            Assert.Contains(tab, mainVm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.Equal("Console.WriteLine(4);", ctrl.State.Editor.Text);
        }
        finally
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Cleanup(h);
        }
    }
}
