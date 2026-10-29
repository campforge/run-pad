using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The tab lifecycle on fakes: no window, no editor, no language services.
public class TabLifecycleManagerTests
{
    private sealed class FakeView(string name) : ITabView
    {
        public bool Running { get; set; }
        public bool Dirty { get; set; }
        public bool Disposed { get; private set; }
        public bool? HighlightingActive { get; private set; }
        public EditorViewState? Restored { get; private set; }
        public EditorViewState State { get; set; } = new(0, 0, 0, 0, []);
        public bool HasRunningProcess => Running;
        public RecoveryTab CaptureRecovery() => new() { EditorText = name, IsDirty = Dirty };
        public EditorViewState CaptureViewState() => State;
        public void RestoreViewState(EditorViewState state) => Restored = state;
        public void SetHighlightingActive(bool active) => HighlightingActive = active;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSurface : ITabSurface
    {
        public List<string> Log { get; } = [];
        public List<Exception> Failures { get; } = [];
        public void ShowView(EditorTabItemViewModel tab, ITabView view) => Log.Add("view " + tab.FileName);
        public void ShowPlaceholder(EditorTabItemViewModel tab) => Log.Add("placeholder " + tab.FileName);
        public Task ReportLoadFailureAsync(EditorTabItemViewModel tab, Exception error)
        {
            Failures.Add(error);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFactory : ITabViewFactory
    {
        public Func<EditorTabItemViewModel, Task<ITabView>> Create { get; set; } = _ => Task.FromResult<ITabView>(new FakeView("new"));
        public Task<ITabView> CreateAsync(EditorTabItemViewModel tab, CancellationToken cancellationToken) => Create(tab);
    }

    private sealed class Harness
    {
        public TabLifecycleManager Manager { get; } = new(new RecentTabsResidencyPolicy());
        public FakeSurface Surface { get; } = new();
        public FakeFactory Factory { get; } = new();
        public EditorTabItemViewModel? Active { get; set; }
        public Dictionary<EditorTabItemViewModel, FakeView> Views { get; } = new();

        public Harness() => Manager.Attach(Surface, Factory, () => Active);

        public EditorTabItemViewModel Open(string name)
        {
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            var tab = new EditorTabItemViewModel(null, [], prefs.Object, new Mock<IEditorTabActions>().Object,
                MdRunLanguage.CSharp, MdRunKind.Statements, name);
            var view = new FakeView(name);
            Views[tab] = view;
            Manager.Register(tab, view);
            Active = tab;
            return tab;
        }
    }

    // tabs[0] and tabs[1] are the two used longest ago of five; the last one is active.
    private static (Harness Harness, List<EditorTabItemViewModel> Tabs) FiveTabs()
    {
        var harness = new Harness();
        var tabs = Enumerable.Range(1, 5).Select(i => harness.Open("tab" + i)).ToList();
        return (harness, tabs);
    }

    [Fact]
    public void Rebalance_Should_UnloadTheTabsBeyondTheLastThree_AndShowPlaceholders()
    {
        var (harness, tabs) = FiveTabs();
        harness.Views[tabs[0]].State = new(5, 1, 2, 7, [new(3, "x > 1", null, true)]);

        harness.Manager.Rebalance();

        Assert.Equal(["placeholder tab1", "placeholder tab2"], harness.Surface.Log.Order());
        Assert.True(harness.Views[tabs[0]].Disposed);
        Assert.True(harness.Views[tabs[1]].Disposed);
        Assert.False(harness.Manager.IsLoaded(tabs[0]));
        Assert.All(tabs.Skip(2), tab => Assert.True(harness.Manager.IsLoaded(tab)));
    }

    [Fact]
    public async Task SelectingAnUnloadedTab_Should_BuildItsViewAgain_WithTheStateItHad()
    {
        var (harness, tabs) = FiveTabs();
        var state = new EditorViewState(5, 1, 2, 7, [new(3, "x > 1", null, true)]);
        harness.Views[tabs[0]].State = state;
        harness.Manager.Rebalance();
        var rebuilt = new FakeView("rebuilt");
        harness.Factory.Create = _ => Task.FromResult<ITabView>(rebuilt);
        harness.Surface.Log.Clear();

        harness.Active = tabs[0];
        harness.Manager.Activated(tabs[0]);
        await Task.Yield();

        Assert.Equal(state, rebuilt.Restored);
        Assert.True(harness.Manager.IsLoaded(tabs[0]));
        Assert.Equal("view tab1", harness.Surface.Log[0]);
        Assert.Equal(3, tabs.Count(harness.Manager.IsLoaded)); // the oldest loaded one made room
    }

    [Fact]
    public void ATabThatIsRunning_Should_NotBeUnloaded()
    {
        var (harness, tabs) = FiveTabs();
        harness.Views[tabs[0]].Running = true;

        harness.Manager.Rebalance();

        Assert.True(harness.Manager.IsLoaded(tabs[0]));
        Assert.False(harness.Manager.IsLoaded(tabs[1]));
    }

    [Fact]
    public void ATabWithUnsavedWork_Should_NotBeUnloaded()
    {
        var (harness, tabs) = FiveTabs();
        harness.Views[tabs[0]].Dirty = true;

        harness.Manager.Rebalance();

        Assert.True(harness.Manager.IsLoaded(tabs[0]));
        Assert.False(harness.Views[tabs[0]].Disposed);
    }

    [Fact]
    public void ARunThatEndedOutOfSight_Should_KeepTheTabLoadedUntilItIsSelected()
    {
        var harness = new Harness();
        var first = harness.Open("tab1");
        first.RunState = RunState.Run;
        var others = Enumerable.Range(2, 4).Select(i => harness.Open("tab" + i)).ToList();

        first.RunState = RunState.Idle; // ended while another tab was shown
        harness.Manager.Rebalance();
        Assert.True(harness.Manager.IsLoaded(first));

        harness.Active = first;
        harness.Manager.Activated(first); // the user looks at it
        harness.Active = others[3];
        foreach (var tab in others.Skip(1)) harness.Manager.Activated(tab);

        Assert.False(harness.Manager.IsLoaded(first));
    }

    [Fact]
    public async Task AViewThatCannotBeBuilt_Should_BeReported_AndTheTabStayUnloadedForTheNextTry()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();
        harness.Factory.Create = _ => throw new InvalidOperationException("no editor");

        harness.Manager.Activated(tabs[0]);
        await Task.Yield();

        Assert.Equal("no editor", Assert.Single(harness.Surface.Failures).Message);
        Assert.False(harness.Manager.IsLoaded(tabs[0]));

        var rebuilt = new FakeView("rebuilt");
        harness.Factory.Create = _ => Task.FromResult<ITabView>(rebuilt);
        harness.Manager.Activated(tabs[0]);
        await Task.Yield();
        Assert.True(harness.Manager.IsLoaded(tabs[0]));
    }

    [Fact]
    public async Task ATabClosedWhileItsViewIsBeingBuilt_Should_HaveThatViewDisposed()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();
        var gate = new TaskCompletionSource<ITabView>();
        harness.Factory.Create = _ => gate.Task;
        harness.Manager.Activated(tabs[0]);

        harness.Manager.Unregister(tabs[0]);
        var late = new FakeView("late");
        gate.SetResult(late);
        await Task.Yield();

        Assert.True(late.Disposed);
        Assert.False(harness.Manager.IsLoaded(tabs[0]));
    }

    [Fact]
    public void ClosingALoadedTab_Should_DisposeItsView()
    {
        var (harness, tabs) = FiveTabs();

        harness.Manager.Unregister(tabs[4]);

        Assert.True(harness.Views[tabs[4]].Disposed);
    }

    [Fact]
    public void OnlyTheActiveTab_Should_Colour()
    {
        var (harness, tabs) = FiveTabs();

        harness.Manager.Rebalance();

        Assert.True(harness.Views[tabs[4]].HighlightingActive);
        Assert.False(harness.Views[tabs[3]].HighlightingActive);
        Assert.False(harness.Views[tabs[2]].HighlightingActive);
    }
}
