using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
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

// TODO.md, "tab lifecycle": a brand-new script starts empty and in-memory-only (no disk
// write until Save/Save As — see MainWindowViewModel.NewRpadFile), and its tab tracks whether it has
// unsaved changes (EditorTabItemViewModel.IsDirty, shown as yellow tab text).
public class EditorTabLifecycleTests
{
    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static Mock<IThemeService> MakeTheme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private static (EditorsTabItemControl Ctrl, EditorTabItemViewModel Vm) CreateControl(
        string? filePath, RpadLanguage newFileLanguage = RpadLanguage.CSharp, RpadKind newFileKind = RpadKind.Statements)
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            filePath, [], prefs.Object, new Mock<IEditorTabActions>().Object, newFileLanguage, newFileKind);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new CSharpReferenceService(), buildCache.Object);
        var host = new Window { Content = ctrl };
        host.Show();
        return (ctrl, vm);
    }

    [AvaloniaFact]
    public void NewUntitledTab_Should_StartEmpty_NotTheOldHelloRPadSample()
    {
        var (ctrl, _) = CreateControl(filePath: null);

        Assert.Equal("", ctrl.State.Editor.Text);
    }

    [AvaloniaFact]
    public void NewUntitledTab_Should_CarryThePendingLanguageAndKind_IntoItsInMemoryFrontMatter()
    {
        var (ctrl, _) = CreateControl(filePath: null, newFileLanguage: RpadLanguage.Vb, newFileKind: RpadKind.Program);

        Assert.NotNull(ctrl.State.RpadDocument);
        Assert.Equal(RpadLanguage.Vb, ctrl.State.RpadDocument!.FrontMatter.Language);
        Assert.Equal(RpadKind.Program, ctrl.State.RpadDocument.FrontMatter.Kind);
    }

    [AvaloniaFact]
    public void NewUntitledTab_Should_NotBeDirty_BeforeAnyEdit()
    {
        var (_, vm) = CreateControl(filePath: null);

        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public void TypingInTheEditor_Should_MarkTheTabDirty()
    {
        var (ctrl, vm) = CreateControl(filePath: null);

        ctrl.State.Editor.Text = "int x = 1;";

        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public void SaveIfNeeded_Should_ClearIsDirty_AfterASuccessfulWrite()
    {
        var tmpDir = Directory.CreateTempSubdirectory("runpad-dirty-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));
            var (ctrl, vm) = CreateControl(filePath);

            ctrl.State.Editor.Text = "Console.WriteLine(2);";
            Assert.True(vm.IsDirty);

            ctrl.SaveIfNeeded();

            Assert.False(vm.IsDirty);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    // SaveIfNeeded is deliberately a no-op for a tab with no path yet (see its own comment) — an
    // untitled tab only gets written to disk via Save As, never implicitly on e.g. tab close.
    [AvaloniaFact]
    public void SaveIfNeeded_Should_NotClearIsDirty_ForAnUntitledTab()
    {
        var (ctrl, vm) = CreateControl(filePath: null);
        ctrl.State.Editor.Text = "int x = 1;";
        Assert.True(vm.IsDirty);

        ctrl.SaveIfNeeded();

        Assert.True(vm.IsDirty);
    }

    // ── MainWindowViewModel.SaveTabAsAsync ──────────────────────────────────────

    private static (MainWindowViewModel Vm, Mock<IWindowService> WindowService, Mock<IFileSystemService> FileSystem) CreateMainVm(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var windowService = new Mock<IWindowService>();
        var prefs = MakePrefs();

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, windowService, fileSystem);
    }

    [Fact]
    public async Task SaveTabAsAsync_Should_WriteAndRebindTheTab_WhenTheDialogReturnsAPath()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-saveas-test").FullName;
        try
        {
            var (mainVm, windowService, _) = CreateMainVm(rootFolder);
            mainVm.NewRpadFile();
            var tab = Assert.Single(mainVm.Tabs);
            Assert.Null(tab.FilePath);

            var chosenPath = Path.Combine(rootFolder, "MyScript.rpad");
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), rootFolder))
                .ReturnsAsync(chosenPath);

            var writtenPaths = new List<string>();
            await mainVm.SaveTabAsAsync(tab, path =>
            {
                writtenPaths.Add(path);
                File.WriteAllText(path, "content");
            });

            Assert.Equal([chosenPath], writtenPaths);
            Assert.True(File.Exists(chosenPath));
            Assert.Equal(chosenPath, tab.FilePath);
            Assert.False(tab.IsDirty);

            // Re-registered under the new path — opening it again returns the SAME tab instead of a
            // second one, same bookkeeping RenameFile already relies on for a file-tree rename.
            mainVm.OpenTab(chosenPath);
            Assert.Same(tab, mainVm.ActiveTab);
            Assert.Single(mainVm.Tabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public async Task SaveTabAsAsync_Should_DoNothing_WhenTheDialogIsCancelled()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-saveas-cancel-test").FullName;
        try
        {
            var (mainVm, windowService, _) = CreateMainVm(rootFolder);
            mainVm.NewRpadFile();
            var tab = Assert.Single(mainVm.Tabs);

            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string?)null);

            var writeCalled = false;
            await mainVm.SaveTabAsAsync(tab, _ => writeCalled = true);

            Assert.False(writeCalled);
            Assert.Null(tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    // ── MainWindowViewModel.DuplicateTab ────────────────────────────────────────

    [Fact]
    public void DuplicateTab_Should_WriteAFreeCopyNameNextToTheOriginal_AndOpenIt()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-duplicate-test").FullName;
        try
        {
            var original = Path.Combine(rootFolder, "Query 1.rpad");
            File.WriteAllText(original, RpadWriter.NewFile("Query 1"));

            var (mainVm, _, _) = CreateMainVm(rootFolder);
            mainVm.OpenTab(original);
            var tab = Assert.Single(mainVm.Tabs);

            var written = new List<string>();
            mainVm.DuplicateTab(tab, path => { File.WriteAllText(path, "dup"); written.Add(path); });

            var expectedCopyPath = Path.Combine(rootFolder, "Query 1 copy.rpad");
            Assert.Equal([expectedCopyPath], written);
            Assert.True(File.Exists(expectedCopyPath));
            Assert.Contains(mainVm.Tabs, t => t.FilePath == expectedCopyPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void DuplicateTab_Should_DoNothing_ForAnUntitledTab()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-duplicate-untitled-test").FullName;
        try
        {
            var (mainVm, _, _) = CreateMainVm(rootFolder);
            mainVm.NewRpadFile();
            var tab = Assert.Single(mainVm.Tabs);

            var writeCalled = false;
            mainVm.DuplicateTab(tab, _ => writeCalled = true);

            Assert.False(writeCalled);
            Assert.Single(mainVm.Tabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
