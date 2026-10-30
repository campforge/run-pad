using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.Services.Tabs;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Moq;
using Xunit;
using static AvaloniaEdit.TextMate.TextMate;

namespace Invoklet.Runtime.UI.Tests;

// Only the tabs the user touched last keep their text and output in memory; the controls of the others stay,
// empty, and get both back from disk when the tab is selected or something needs the text. A tab with a run
// in progress, or whose run ended while it was out of sight, keeps its content.
public class EditorTabsControlResidencyTests
{
    private sealed class FixedSessionFolder(string folder) : ISessionFolder
    {
        public string? SessionFolder => folder;
    }

    // Reads can be made to fail, as a deleted or damaged file would.
    private sealed class FlakySpill(ITabSpillStore inner) : ITabSpillStore
    {
        public bool FailReads { get; set; }
        public bool TryWrite(Guid tab, ReleasedContent content) => inner.TryWrite(tab, content);
        public ReleasedContent? TryRead(Guid tab) => FailReads ? null : inner.TryRead(tab);
        public void Delete(Guid tab) => inner.Delete(tab);
        public void DeleteAll() => inner.DeleteAll();
    }

    private static (EditorTabsControl Tabs, MainWindowViewModel Vm, string Folder, FlakySpill Spill) CreateHarness(
        Mock<IWindowService>? windowService = null, Mock<IBuildCacheService>? buildCache = null, string? folder = null)
    {
        folder ??= Directory.CreateTempSubdirectory("Invoklet-residency-test").FullName;
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var windows = windowService ?? new Mock<IWindowService>();
        var vm = new MainWindowViewModel([], fileSystem.Object, windows.Object, prefs.Object, new LanguageHostRegistry());
        var spill = new FlakySpill(new SessionSpillStore(new FixedSessionFolder(Path.Combine(folder, ".session"))));
        var tabs = new EditorTabsControl(theme.Object, windows.Object,
            new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object),
            prefs.Object, (buildCache ?? new Mock<IBuildCacheService>()).Object,
            lifecycle: new TabLifecycleManager(new RecentTabsResidencyPolicy(), spill));
        new Window { Content = tabs }.Show();
        tabs.Attach(vm);
        return (tabs, vm, folder, spill);
    }

    private static int Loaded(EditorTabsControl tabs, MainWindowViewModel vm) => vm.Tabs.Count(tabs.IsLoadedForTests);

    private static string EditorText(EditorTabsControl tabs, EditorTabItemViewModel tab) => tabs.ControlForTests(tab)!.State.Editor.Text;

    // A new tab is shown as soon as its editor services exist; wait until it and the releasing it causes are done.
    private static async Task AddTabAsync(EditorTabsControl tabs, MainWindowViewModel vm)
    {
        var count = vm.Tabs.Count;
        vm.NewMdRunFile();
        await TestWait.WhileAsync(() => vm.Tabs.Count <= count || tabs.ControlForTests(vm.Tabs[^1]) is null);
        await TestWait.SettleAsync(() => Loaded(tabs, vm));
    }

    private static async Task AddTabsAsync(EditorTabsControl tabs, MainWindowViewModel vm, int count)
    {
        for (var i = 0; i < count; i++) await AddTabAsync(tabs, vm);
    }

    // The content is read back off the UI thread: wait until it is there.
    private static async Task SelectAsync(EditorTabsControl tabs, MainWindowViewModel vm, EditorTabItemViewModel tab)
    {
        vm.ActiveTab = tab;
        await TestWait.WhileAsync(() => !tabs.IsLoadedForTests(tab));
        await TestWait.SettleAsync(() => Loaded(tabs, vm));
    }

    private static Task<EditorTabItemViewModel> FirstTabAsync(EditorTabsControl tabs, MainWindowViewModel vm)
        => TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null).ContinueWith(_ => vm.Tabs[0]);

    private static string Script(string code) => MdRunWriter.NewFile(code, "A");

    [AvaloniaFact]
    public async Task Tabs_Should_KeepOnlyTheThreeUsedLastLoaded_ButEveryControlStays()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            await FirstTabAsync(tabs, vm);
            await AddTabsAsync(tabs, vm, 4);

            Assert.Equal(5, vm.Tabs.Count);
            Assert.Equal(3, Loaded(tabs, vm));
            Assert.All(vm.Tabs, tab => Assert.NotNull(tabs.ControlForTests(tab)));
            Assert.All(vm.Tabs.Take(2), tab => Assert.False(tabs.IsLoadedForTests(tab)));
            Assert.All(vm.Tabs.Skip(2), tab => Assert.True(tabs.IsLoadedForTests(tab)));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SelectingATabWhoseContentIsReleased_Should_BringItBack_AndReleaseTheOldestLoadedOne()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            await FirstTabAsync(tabs, vm);
            await AddTabsAsync(tabs, vm, 4);

            await SelectAsync(tabs, vm, vm.Tabs[0]);

            Assert.True(tabs.IsLoadedForTests(vm.Tabs[0]));
            Assert.False(tabs.IsLoadedForTests(vm.Tabs[2])); // the oldest of the three that were loaded
            Assert.Equal(3, Loaded(tabs, vm));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ATabWithARunInProgress_Should_KeepItsContent()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var running = await FirstTabAsync(tabs, vm);
            running.RunState = RunState.Run;

            await AddTabsAsync(tabs, vm, 4);

            Assert.True(tabs.IsLoadedForTests(running));
            Assert.Equal(4, Loaded(tabs, vm));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ATabWhoseRunEndedOutOfSight_Should_KeepItsContentUntilTheUserVisitsIt()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var finished = await FirstTabAsync(tabs, vm);
            finished.RunState = RunState.Run;
            await AddTabsAsync(tabs, vm, 4);

            finished.RunState = RunState.Idle; // ended while another tab was shown
            await AddTabAsync(tabs, vm);
            Assert.True(tabs.IsLoadedForTests(finished));

            await SelectAsync(tabs, vm, finished); // the user looks at the result ...
            foreach (var other in vm.Tabs.Skip(3).Take(3)) await SelectAsync(tabs, vm, other); // ... and moves on

            Assert.False(tabs.IsLoadedForTests(finished));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AnEditedTab_Should_Release_ItsText_AndGetItBackUnsaved()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var edited = await FirstTabAsync(tabs, vm);
            tabs.ControlForTests(edited)!.State.Editor.Text = "var unsaved = 1;";
            Assert.True(edited.IsDirty);

            await AddTabsAsync(tabs, vm, 4);

            Assert.False(tabs.IsLoadedForTests(edited));
            Assert.Equal("", EditorText(tabs, edited)); // it is not held in memory
            Assert.True(edited.IsDirty); // and the tab still shows it as unsaved

            await SelectAsync(tabs, vm, edited);

            Assert.Equal("var unsaved = 1;", EditorText(tabs, edited));
            Assert.True(edited.IsDirty);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AReleasedTab_Should_KeepItsBreakpoints_AndGetItsCaretAndOutputBack()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\nvar b = 2;\nvar c = 3;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            var control = tabs.ControlForTests(file)!;
            var editor = control.State.Editor;
            editor.AddBreakpoint(2);
            editor.Breakpoints.Single().Condition = "a > 0";
            editor.CaretOffset = 4;
            control.State.Output.Append("<div>result 42</div>");

            await AddTabsAsync(tabs, vm, 4);
            Assert.False(tabs.IsLoadedForTests(file));
            Assert.Equal("", control.State.Output.Content);
            Assert.Equal("a > 0", Assert.Single(editor.Breakpoints).Condition); // the gutter is not released

            await SelectAsync(tabs, vm, file);

            Assert.Equal(4, editor.CaretOffset);
            Assert.Equal("<div>result 42</div>", control.State.Output.Content);
            Assert.Equal(2, Assert.Single(editor.Breakpoints).Line);
            Assert.False(file.IsDirty);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ASavedTab_Should_ShowWhatItsFileHoldsNow_WhenSelectedAgain()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            await AddTabsAsync(tabs, vm, 4);
            Assert.False(tabs.IsLoadedForTests(file));

            File.WriteAllText(path, Script("var changed = 2;\n"));
            await SelectAsync(tabs, vm, file);

            Assert.Contains("var changed = 2;", EditorText(tabs, file));
            Assert.False(file.IsDirty);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SavingAReleasedEditedTab_Should_WriteItsText_NotAnEmptyEditor()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            tabs.ControlForTests(file)!.State.Editor.Text = "var edited = 3;\n";
            await AddTabsAsync(tabs, vm, 4);
            Assert.False(tabs.IsLoadedForTests(file));

            await file.EnsureContent(); // what saving does first
            file.WriteToFile!(path);

            Assert.Contains("var edited = 3;", File.ReadAllText(path));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TheCheckpointOfAReleasedTab_Should_HaveItsText()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var edited = await FirstTabAsync(tabs, vm);
            tabs.ControlForTests(edited)!.State.Editor.Text = "var unsaved = 1;";
            await AddTabsAsync(tabs, vm, 4);
            Assert.False(tabs.IsLoadedForTests(edited));

            var snapshot = edited.CaptureRecovery!();

            Assert.Equal("var unsaved = 1;", snapshot.EditorText);
            Assert.True(snapshot.IsDirty);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TextThatCannotBeReadBack_Should_BeReported_AndNeverBeReplacedByAnEmptyEditor()
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var (tabs, vm, folder, spill) = CreateHarness(windows);
        try
        {
            var edited = await FirstTabAsync(tabs, vm);
            var control = tabs.ControlForTests(edited)!;
            control.State.Editor.Text = "var unsaved = 1;";
            await AddTabsAsync(tabs, vm, 4);
            spill.FailReads = true;

            vm.ActiveTab = edited;
            await TestWait.WhileAsync(() => windows.Invocations.Count(i => i.Method.Name == nameof(IWindowService.ShowErrorAsync)) == 0);

            windows.Verify(w => w.ShowErrorAsync("Tab Not Loaded", It.IsAny<string>()), Times.Once);
            Assert.False(tabs.IsLoadedForTests(edited));
            Assert.Throws<IOException>(() => control.WriteFile(Path.Combine(folder, "out.inv")));
            Assert.Throws<InvalidOperationException>(() => edited.CaptureRecovery!());
            Assert.True(control.State.Editor.IsReadOnly);

            spill.FailReads = false;
            await SelectAsync(tabs, vm, vm.Tabs[1]);
            await SelectAsync(tabs, vm, edited);
            Assert.Equal("var unsaved = 1;", control.State.Editor.Text); // the next try works
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task OnlyTheTabShown_Should_ColourItsText()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            await FirstTabAsync(tabs, vm);
            await AddTabAsync(tabs, vm);
            var (first, second) = (vm.Tabs[0], vm.Tabs[1]);
            Installation Highlighting(EditorTabItemViewModel tab) => tabs.ControlForTests(tab)!.State.Editor.TextMateInstallation!;
            await TestWait.WhileAsync(() => tabs.ControlForTests(second)!.State.Editor.TextMateInstallation is null);

            Assert.True(Highlighting(first).IsFrozen);
            Assert.False(Highlighting(second).IsFrozen);

            await SelectAsync(tabs, vm, first);

            Assert.False(Highlighting(first).IsFrozen);
            Assert.True(Highlighting(second).IsFrozen);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    // A saved script on disk that is edited, and then released by opening more tabs.
    private static async Task<EditorTabItemViewModel> ReleasedEditedFileAsync(
        EditorTabsControl tabs, MainWindowViewModel vm, string folder)
    {
        var path = Path.Combine(folder, "A.inv");
        File.WriteAllText(path, Script("var a = 1;\n"));
        vm.OpenTab(path);
        await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
        var file = vm.Tabs[0];
        tabs.ControlForTests(file)!.State.Editor.Text = "var edited = 3;\n";
        await AddTabsAsync(tabs, vm, 4);
        Assert.False(tabs.IsLoadedForTests(file));
        return file;
    }

    [AvaloniaFact]
    public async Task SaveAs_OfAReleasedEditedTab_Should_WriteItsText_NotAnEmptyEditor()
    {
        var windows = new Mock<IWindowService>();
        var (tabs, vm, folder, _) = CreateHarness(windows);
        try
        {
            var file = await ReleasedEditedFileAsync(tabs, vm, folder);
            var target = Path.Combine(folder, "B.inv");
            windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(target);

            await file.SaveAsCommand.Execute();

            Assert.Contains("var edited = 3;", File.ReadAllText(target));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task Duplicate_OfAReleasedEditedTab_Should_WriteItsText_NotAnEmptyEditor()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var file = await ReleasedEditedFileAsync(tabs, vm, folder);
            var copy = Path.Combine(folder, "A copy.inv");

            file.DuplicateCommand.Execute().Subscribe();
            await TestWait.WhileAsync(() => !File.Exists(copy));

            Assert.Contains("var edited = 3;", File.ReadAllText(copy));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TheOutputOfARun_Should_WaitInTheFolderOfThatRun_NotInTheSpill()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-residency-test").FullName;
        var runFolder = Path.Combine(folder, "run");
        var cache = new Mock<IBuildCacheService>();
        cache.Setup(c => c.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>())).Returns(runFolder);
        var (tabs, vm, _, spill) = CreateHarness(buildCache: cache, folder: folder);
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            var output = tabs.ControlForTests(file)!.State.Output;
            output.Append("<div>result 42</div>");

            await AddTabsAsync(tabs, vm, 4);

            Assert.True(File.Exists(Path.Combine(runFolder, "run-output.json")));
            Assert.Null(spill.TryRead(file.RecoveryId)!.Output);
            Assert.Equal("", output.Content);

            await SelectAsync(tabs, vm, file);

            Assert.Equal("<div>result 42</div>", output.Content);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ATabOpenedAgain_Should_ShowTheOutputOfTheLastRunOfItsScript()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-residency-test").FullName;
        var runFolder = Path.Combine(folder, "run");
        RunOutputFile.Save(runFolder, new TabOutputState("<div>old run</div>", true, new Dictionary<string, string>()));
        var cache = new Mock<IBuildCacheService>();
        cache.Setup(c => c.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>())).Returns(runFolder);
        var (tabs, vm, _, _) = CreateHarness(buildCache: cache, folder: folder);
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);

            var state = tabs.ControlForTests(vm.Tabs[0])!.State;

            Assert.Equal("<div>old run</div>", state.Output.Content);
            Assert.True(state.HadRunError);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ASavedTabWhoseFileIsGone_Should_KeepItsTextAsUnsaved_WhenSelectedAgain()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            var text = EditorText(tabs, file);
            await AddTabsAsync(tabs, vm, 4);
            Assert.False(tabs.IsLoadedForTests(file));
            File.Delete(path);

            await SelectAsync(tabs, vm, file);

            Assert.Equal(text, EditorText(tabs, file)); // not an empty editor
            Assert.True(file.IsDirty); // and no longer the file's
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AskedTwiceWhileItIsBeingRead_Should_RestoreOnce()
    {
        var (tabs, vm, folder, _) = CreateHarness();
        try
        {
            var edited = await FirstTabAsync(tabs, vm);
            tabs.ControlForTests(edited)!.State.Editor.Text = "var unsaved = 1;";
            await AddTabsAsync(tabs, vm, 4);
            var control = tabs.ControlForTests(edited)!;
            Assert.True(control.IsContentReleased);

            var first = control.TryRestoreContentAsync();
            var second = control.TryRestoreContentAsync();

            Assert.Same(first, second);
            Assert.True(await first);
            Assert.Equal("var unsaved = 1;", control.State.Editor.Text);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TheOutputOfAnUnsavedTab_Should_GoToTheSpill_AndComeBackInTheSession()
    {
        var (tabs, vm, folder, spill) = CreateHarness(); // no build folder, as an unsaved script has none
        try
        {
            var untitled = await FirstTabAsync(tabs, vm);
            var output = tabs.ControlForTests(untitled)!.State.Output;
            output.Append("<div>untitled output</div>");

            await AddTabsAsync(tabs, vm, 4);

            Assert.Equal("", output.Content);
            Assert.Equal("<div>untitled output</div>", spill.TryRead(untitled.RecoveryId)!.Output!.Content);

            await SelectAsync(tabs, vm, untitled);

            Assert.Equal("<div>untitled output</div>", output.Content);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SaveAs_Should_MoveTheRunFolder_SoTheOldFileKeepsOnlyItsOwnOutput()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-residency-test").FullName;
        var cache = new Mock<IBuildCacheService>();
        cache.Setup(c => c.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string path, string _) => Path.Combine(folder, "run-" + Path.GetFileNameWithoutExtension(path)));
        var windows = new Mock<IWindowService>();
        var (tabs, vm, _, _) = CreateHarness(windows, cache, folder);
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, Script("var a = 1;\n"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || tabs.ControlForTests(vm.Tabs[0]) is null || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            var control = tabs.ControlForTests(file)!;
            control.State.Output.Append("<div>run of A</div>");
            await AddTabsAsync(tabs, vm, 4); // releasing the content keeps the output in A's folder
            await SelectAsync(tabs, vm, file);
            var runOfA = Path.Combine(folder, "run-A");
            Assert.Equal("<div>run of A</div>", RunOutputFile.TryLoad(runOfA)!.Content);

            windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Path.Combine(folder, "B.inv"));
            await file.SaveAsCommand.Execute();
            control.State.Output.Append("<div>run of B</div>");
            await AddTabsAsync(tabs, vm, 4);

            Assert.Equal("<div>run of A</div>", RunOutputFile.TryLoad(runOfA)!.Content); // untouched by B
            Assert.Contains("run of B", RunOutputFile.TryLoad(Path.Combine(folder, "run-B"))!.Content);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }
}
