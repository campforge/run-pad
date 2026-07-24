using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md, "Жизненный цикл вкладок (остаток)": closing an untitled tab with real typed content used
// to discard it silently — SaveIfNeeded is a no-op without a FilePath, so nothing about closing an
// untitled tab ever asked first. EditorTabsControl.RequestCloseTabAsync (wired to both the tab's [x]
// button and Ctrl+W — see EditorTabsControl.CloseActiveTab) now prompts Save/Don't Save/Cancel for
// that one specific case. These exercise the real close path end-to-end (a genuine EditorTabsControl
// + EditorsTabItemControl + CodeTextEditor, not just the ViewModel-level flags) against a mocked
// IWindowService, so the dialog's answer is scripted but everything downstream of it is real.
public class EditorTabsControlCloseConfirmationTests
{
    private static (EditorTabsControl EditorTabs, MainWindowViewModel MainVm, Mock<IWindowService> WindowService, string RootFolder)
        CreateHarness()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-close-confirm-test").FullName;

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

        return (editorTabs, mainVm, windowService, rootFolder);
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
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingCancel_Should_KeepTheTabOpen()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingDontSave_Should_DiscardAndClose()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndChoosingSave_Should_WriteItThenClose()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
            for (var i = 0; i < 100 && !File.Exists(chosenPath); i++)
                await Task.Delay(20);

            Assert.True(File.Exists(chosenPath));
            Assert.Contains("Console.WriteLine(1);", File.ReadAllText(chosenPath));
            Assert.Empty(mainVm.Tabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyUntitledTab_AndCancellingTheSaveAsDialog_Should_KeepTheTabOpen()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyTabWithARealPath_Should_NotShowADialog()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
        try
        {
            var filePath = Path.Combine(rootFolder, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            mainVm.OpenTab(filePath);
            for (var i = 0; i < 100 && mainVm.ActiveTab?.FrontMatter is null; i++)
                await Task.Delay(20);
            var tab = mainVm.Tabs.Single(t => t.FilePath == filePath);

            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .Single(c => ReferenceEquals(c.DataContext, tab));
            ctrl.State.Editor.Text = "Console.WriteLine(2);";
            Assert.True(tab.IsDirty);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            // A tab with a real path already gets saved on close (SaveIfNeeded, unconditional) —
            // nothing to confirm.
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.Empty(mainVm.Tabs);
            Assert.Contains("Console.WriteLine(2);", File.ReadAllText(filePath));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    // ── App-exit confirmation (MainWindow.Closing → HasUnsavedUntitledTabs/ConfirmCloseAllUnsavedTabsAsync) ──

    [AvaloniaFact]
    public async Task HasUnsavedUntitledTabs_Should_BeFalse_ForAPristineUntitledTab()
    {
        var (editorTabs, _, _, rootFolder) = CreateHarness();
        try
        {
            await GetSoleControlAsync(editorTabs);
            Assert.False(editorTabs.HasUnsavedUntitledTabs());
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task HasUnsavedUntitledTabs_Should_BeTrue_OnceTheUntitledTabIsEdited()
    {
        var (editorTabs, _, _, rootFolder) = CreateHarness();
        try
        {
            var ctrl = await GetSoleControlAsync(editorTabs);
            ctrl.State.Editor.Text = "Console.WriteLine(1);";

            Assert.True(editorTabs.HasUnsavedUntitledTabs());
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnTrue_AndAskNothing_WhenNothingIsDirty()
    {
        var (editorTabs, _, windowService, rootFolder) = CreateHarness();
        try
        {
            await GetSoleControlAsync(editorTabs);

            var canClose = await editorTabs.ConfirmCloseAllUnsavedTabsAsync();

            Assert.True(canClose);
            windowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnFalse_WhenUserCancels()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_ReturnTrue_WhenUserChoosesDontSave()
    {
        var (editorTabs, _, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_SaveIt_WhenUserChoosesSave()
    {
        var (editorTabs, _, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAllUnsavedTabsAsync_Should_AskAboutEveryDirtyUntitledTab_NotJustTheFirst()
    {
        var (editorTabs, mainVm, windowService, rootFolder) = CreateHarness();
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
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
