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
        Mock<IWindowService>? windowService = null)
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-residency-test").FullName;
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
        var spill = new FlakySpill(new TempFileTabSpillStore(Path.Combine(folder, ".spill")));
        var tabs = new EditorTabsControl(theme.Object, windows.Object,
            new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object),
            prefs.Object, new Mock<IBuildCacheService>().Object,
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

    private static void Select(EditorTabsControl tabs, MainWindowViewModel vm, EditorTabItemViewModel tab) => vm.ActiveTab = tab;

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

            Select(tabs, vm, vm.Tabs[0]);

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

            Select(tabs, vm, finished); // the user looks at the result ...
            foreach (var other in vm.Tabs.Skip(3).Take(3)) Select(tabs, vm, other); // ... and moves on

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

            Select(tabs, vm, edited);

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

            Select(tabs, vm, file);

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
            Select(tabs, vm, file);

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

            Select(tabs, vm, edited);

            windows.Verify(w => w.ShowErrorAsync("Tab Not Loaded", It.IsAny<string>()), Times.Once);
            Assert.False(tabs.IsLoadedForTests(edited));
            Assert.Throws<IOException>(() => control.WriteFile(Path.Combine(folder, "out.inv")));
            Assert.Throws<InvalidOperationException>(() => edited.CaptureRecovery!());
            Assert.True(control.State.Editor.IsReadOnly);

            spill.FailReads = false;
            Select(tabs, vm, vm.Tabs[1]);
            Select(tabs, vm, edited);
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

            Select(tabs, vm, first);

            Assert.False(Highlighting(first).IsFrozen);
            Assert.True(Highlighting(second).IsFrozen);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }
}
