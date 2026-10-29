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
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Only the tabs the user touched last keep their editor; the others show just their header and are
// built again from their file when selected. A tab with a run in progress, one whose run ended while it
// was out of sight, and one with unsaved work stay loaded.
public class EditorTabsControlResidencyTests
{
    private static (EditorTabsControl Tabs, MainWindowViewModel Vm, string Folder) CreateHarness()
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
        var vm = new MainWindowViewModel([], fileSystem.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object),
            prefs.Object, new Mock<IBuildCacheService>().Object, residency: new TabResidencyManager());
        new Window { Content = tabs }.Show();
        tabs.Attach(vm);
        return (tabs, vm, folder);
    }

    private static int Loaded(EditorTabsControl tabs, MainWindowViewModel vm) => vm.Tabs.Count(tabs.IsLoadedForTests);

    // A new tab is shown as soon as its editor services exist; wait until it and the unloading it causes are done.
    private static async Task AddTabAsync(EditorTabsControl tabs, MainWindowViewModel vm)
    {
        var count = vm.Tabs.Count;
        vm.NewMdRunFile();
        await TestWait.WhileAsync(() => vm.Tabs.Count <= count || !tabs.IsLoadedForTests(vm.Tabs[^1]));
        await TestWait.SettleAsync(() => Loaded(tabs, vm));
    }

    private static async Task SelectAsync(EditorTabsControl tabs, MainWindowViewModel vm, EditorTabItemViewModel tab)
    {
        vm.ActiveTab = tab;
        await TestWait.WhileAsync(() => !tabs.IsLoadedForTests(tab));
        await TestWait.SettleAsync(() => Loaded(tabs, vm));
    }

    [AvaloniaFact]
    public async Task Tabs_Should_KeepOnlyTheThreeUsedLastLoaded()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]));
            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);

            Assert.Equal(5, vm.Tabs.Count);
            Assert.Equal(3, Loaded(tabs, vm));
            Assert.All(vm.Tabs.Take(2), tab => Assert.False(tabs.IsLoadedForTests(tab)));
            Assert.All(vm.Tabs.Skip(2), tab => Assert.True(tabs.IsLoadedForTests(tab)));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SelectingAnUnloadedTab_Should_LoadItAgain_AndUnloadTheOldestLoadedOne()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]));
            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);

            await SelectAsync(tabs, vm, vm.Tabs[0]);

            Assert.True(tabs.IsLoadedForTests(vm.Tabs[0]));
            Assert.False(tabs.IsLoadedForTests(vm.Tabs[2])); // the oldest of the three that were loaded
            Assert.Equal(3, Loaded(tabs, vm));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ATabWithARunInProgress_Should_NotBeUnloaded()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]));
            var running = vm.Tabs[0];
            running.RunState = RunState.Run;

            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);

            Assert.True(tabs.IsLoadedForTests(running));
            Assert.Equal(4, Loaded(tabs, vm));
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ATabWhoseRunEndedOutOfSight_Should_StayLoadedUntilTheUserVisitsIt()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]));
            var finished = vm.Tabs[0];
            finished.RunState = RunState.Run;
            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);

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
    public async Task ATabWithUnsavedWork_Should_NotBeUnloaded()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]));
            var edited = vm.Tabs[0];
            tabs.ControlForTests(edited)!.State.Editor.Text = "var unsaved = 1;";

            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);

            Assert.True(tabs.IsLoadedForTests(edited));
            Assert.Equal("var unsaved = 1;", tabs.ControlForTests(edited)!.State.Editor.Text);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AReloadedTab_Should_KeepItsBreakpoints_AndItsCaret()
    {
        var (tabs, vm, folder) = CreateHarness();
        try
        {
            var path = Path.Combine(folder, "A.inv");
            File.WriteAllText(path, MdRunWriter.NewFile("var a = 1;\nvar b = 2;\nvar c = 3;\n", "A"));
            vm.OpenTab(path);
            await TestWait.WhileAsync(() => vm.Tabs.Count == 0 || !tabs.IsLoadedForTests(vm.Tabs[0]) || vm.Tabs[0].FilePath is null);
            var file = vm.Tabs[0];
            var editor = tabs.ControlForTests(file)!.State.Editor;
            editor.AddBreakpoint(2);
            editor.Breakpoints.Single().Condition = "a > 0";
            editor.Breakpoints.Single().IsEnabled = false;
            editor.CaretOffset = 4;

            for (var i = 0; i < 4; i++) await AddTabAsync(tabs, vm);
            Assert.False(tabs.IsLoadedForTests(file));

            await SelectAsync(tabs, vm, file);

            var reloaded = tabs.ControlForTests(file)!.State.Editor;
            var breakpoint = Assert.Single(reloaded.Breakpoints);
            Assert.Equal(2, breakpoint.Line);
            Assert.Equal("a > 0", breakpoint.Condition);
            Assert.False(breakpoint.IsEnabled);
            Assert.Equal(4, reloaded.CaretOffset);
        }
        finally { tabs.Dispose(); Directory.Delete(folder, true); }
    }
}
