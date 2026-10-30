using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The tab lifecycle on fakes: no window, no editor, no files.
public class TabLifecycleManagerTests
{
    private sealed class FakeView : ITabView
    {
        public bool Running { get; set; }
        public bool CanRelease { get; set; } = true;
        public bool CanRestore { get; set; } = true;
        public bool IsContentReleased { get; private set; }
        public bool? HighlightingActive { get; private set; }
        public int Released { get; private set; }
        public int Restored { get; private set; }
        public bool HasRunningProcess => Running;

        public bool TryReleaseContent()
        {
            if (!CanRelease || Running) return false;
            IsContentReleased = true;
            Released++;
            return true;
        }

        public bool TryRestoreContent()
        {
            if (!CanRestore) return false;
            IsContentReleased = false;
            Restored++;
            return true;
        }

        public void SetHighlightingActive(bool active) => HighlightingActive = active;
    }

    private sealed class FakeSpill : ITabSpillStore
    {
        public bool DeletedAll { get; private set; }
        public bool TryWrite(Guid tab, ReleasedContent content) => true;
        public ReleasedContent? TryRead(Guid tab) => null;
        public void Delete(Guid tab) { }
        public void DeleteAll() => DeletedAll = true;
    }

    private sealed class Harness
    {
        public FakeSpill Spill { get; } = new();
        public TabLifecycleManager Manager { get; }
        public EditorTabItemViewModel? Active { get; set; }
        public List<EditorTabItemViewModel> NotRestored { get; } = [];
        public Dictionary<EditorTabItemViewModel, FakeView> Views { get; } = new();

        public Harness()
        {
            Manager = new(new RecentTabsResidencyPolicy(), Spill);
            Manager.Attach(() => Active, NotRestored.Add);
        }

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
            var view = new FakeView();
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
    public void Rebalance_Should_ReleaseTheContentOfTheTabsBeyondTheLastThree()
    {
        var (harness, tabs) = FiveTabs();

        harness.Manager.Rebalance();

        Assert.True(harness.Views[tabs[0]].IsContentReleased);
        Assert.True(harness.Views[tabs[1]].IsContentReleased);
        Assert.All(tabs.Skip(2), tab => Assert.True(harness.Manager.IsContentLoaded(tab)));
        Assert.False(harness.Manager.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void SelectingATabWhoseContentIsReleased_Should_BringItBack_AndReleaseTheOldestLoadedOne()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();

        harness.Active = tabs[0];
        harness.Manager.Activated(tabs[0]);

        Assert.Equal(1, harness.Views[tabs[0]].Restored);
        Assert.True(harness.Manager.IsContentLoaded(tabs[0]));
        Assert.Equal(3, tabs.Count(harness.Manager.IsContentLoaded)); // the oldest loaded one made room
        Assert.True(harness.Views[tabs[2]].IsContentReleased);
    }

    [Fact]
    public async Task WhatNeedsTheText_Should_BringItBackWithoutSelectingTheTab()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();

        await tabs[0].EnsureContent();

        Assert.False(harness.Views[tabs[0]].IsContentReleased);
    }

    [Fact]
    public void ATabThatIsRunning_Should_NotBeReleased()
    {
        var (harness, tabs) = FiveTabs();
        harness.Views[tabs[0]].Running = true;

        harness.Manager.Rebalance();

        Assert.True(harness.Manager.IsContentLoaded(tabs[0]));
        Assert.False(harness.Manager.IsContentLoaded(tabs[1]));
    }

    [Fact]
    public void ARunThatEndedOutOfSight_Should_KeepTheContentUntilTheTabIsSelected()
    {
        var harness = new Harness();
        var first = harness.Open("tab1");
        first.RunState = RunState.Run;
        var others = Enumerable.Range(2, 4).Select(i => harness.Open("tab" + i)).ToList();

        first.RunState = RunState.Idle; // ended while another tab was shown
        harness.Manager.Rebalance();
        Assert.True(harness.Manager.IsContentLoaded(first));

        harness.Active = first;
        harness.Manager.Activated(first); // the user looks at it
        harness.Active = others[3];
        foreach (var tab in others.Skip(1)) harness.Manager.Activated(tab);

        Assert.False(harness.Manager.IsContentLoaded(first));
    }

    [Fact]
    public void AViewThatCannotReleaseItsContent_Should_StayLoaded()
    {
        var (harness, tabs) = FiveTabs();
        harness.Views[tabs[0]].CanRelease = false;

        harness.Manager.Rebalance();

        Assert.True(harness.Manager.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void ContentThatCannotBeRead_Should_BeReported_AndTheTabStayReleasedForTheNextTry()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();
        harness.Views[tabs[0]].CanRestore = false;

        harness.Active = tabs[0];
        harness.Manager.Activated(tabs[0]);

        Assert.Equal([tabs[0]], harness.NotRestored);
        Assert.False(harness.Manager.IsContentLoaded(tabs[0]));

        harness.Views[tabs[0]].CanRestore = true;
        harness.Manager.Activated(tabs[0]);
        Assert.True(harness.Manager.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public async Task AClosedTab_Should_NoLongerBeKnown()
    {
        var (harness, tabs) = FiveTabs();
        harness.Manager.Rebalance();

        harness.Manager.Unregister(tabs[0]);
        await tabs[0].EnsureContent();

        Assert.Equal(0, harness.Views[tabs[0]].Restored);
        Assert.False(harness.Manager.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void OnlyTheActiveTab_Should_Colour()
    {
        var (harness, tabs) = FiveTabs();

        harness.Manager.Rebalance();

        Assert.True(harness.Views[tabs[4]].HighlightingActive);
        Assert.False(harness.Views[tabs[3]].HighlightingActive);
        Assert.False(harness.Views[tabs[0]].HighlightingActive);
    }

    [Fact]
    public void DetachingTheStrip_Should_RemoveWhatWasKept()
    {
        var (harness, _) = FiveTabs();

        harness.Manager.Detach();

        Assert.True(harness.Spill.DeletedAll);
    }
}
