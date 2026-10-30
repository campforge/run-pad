using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class TabLifecycleManagerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    // Opens the tabs the way the strip does: a tab is registered with its view and becomes the active one.
    private static List<EditorTabItemViewModel> Open(TabLifecycleManagerFixture fixture, TabLifecycleManager sut, params string[] names)
    {
        sut.Attach(fixture.ActiveTabSource, fixture.OnNotRestored);
        var tabs = new List<EditorTabItemViewModel>();
        foreach (var name in names)
        {
            var tab = fixture.CreateTab(name);
            sut.Register(tab, fixture.Views[tab].View.Object);
            fixture.Active = tab;
            tabs.Add(tab);
        }
        return tabs;
    }

    [Fact]
    public void Rebalance_PolicyGivesUpTabs_ReleasesTheirContent()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b", "c");
        fixture.PolicyGivesUp(tabs[0], tabs[1]);

        // Act
        sut.Rebalance();

        // Assert
        fixture.Views[tabs[0]].View.Verify(v => v.TryReleaseContent(), Times.Once);
        fixture.Views[tabs[1]].View.Verify(v => v.TryReleaseContent(), Times.Once);
        fixture.Views[tabs[2]].View.Verify(v => v.TryReleaseContent(), Times.Never);
        Assert.False(sut.IsContentLoaded(tabs[0]));
        Assert.True(sut.IsContentLoaded(tabs[2]));
    }

    [Fact]
    public void Rebalance_TabIsActive_IsNeverReleased()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        fixture.PolicyGivesUp(tabs[1]); // the policy would, but b is the one being looked at

        // Act
        sut.Rebalance();

        // Assert
        fixture.Views[tabs[1]].View.Verify(v => v.TryReleaseContent(), Times.Never);
    }

    [Fact]
    public void Rebalance_TabIsRunning_IsNotReleased()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        tabs[0].RunState = RunState.Run;
        fixture.PolicyGivesUp(tabs[0]);

        // Act
        sut.Rebalance();

        // Assert
        fixture.Views[tabs[0]].View.Verify(v => v.TryReleaseContent(), Times.Never);
    }

    [Fact]
    public void Rebalance_RunEndedOutOfSight_KeepsTheContentUntilTheTabIsSelected()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        tabs[0].RunState = RunState.Run;
        tabs[0].RunState = RunState.Idle; // ended while b was shown
        fixture.PolicyGivesUp(tabs[0]);

        // Act
        sut.Rebalance();
        var releasedBeforeTheVisit = fixture.Views[tabs[0]].Released;
        fixture.Active = tabs[0];
        sut.Activated(tabs[0]); // the user looks at it
        fixture.Active = tabs[1];
        sut.Rebalance();

        // Assert
        Assert.False(releasedBeforeTheVisit);
        Assert.True(fixture.Views[tabs[0]].Released);
    }

    [Fact]
    public void Rebalance_ViewCannotReleaseItsContent_TabStaysLoaded()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        fixture.Views[tabs[0]].CanRelease = false;
        fixture.PolicyGivesUp(tabs[0]);

        // Act
        sut.Rebalance();

        // Assert
        Assert.True(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void Rebalance_Called_ColoursOnlyTheActiveTab()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b", "c");

        // Act
        sut.Rebalance();

        // Assert
        fixture.Views[tabs[2]].View.Verify(v => v.SetHighlightingActive(true), Times.Once);
        fixture.Views[tabs[0]].View.Verify(v => v.SetHighlightingActive(false), Times.Once);
        fixture.Views[tabs[1]].View.Verify(v => v.SetHighlightingActive(false), Times.Once);
    }

    [Fact]
    public void Register_Tab_TouchesItInThePolicy()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();

        // Act
        var tabs = Open(fixture, sut, "a");

        // Assert
        fixture.Policy.Verify(p => p.Touch(tabs[0]), Times.Once);
    }

    [Fact]
    public void Activated_ReleasedTab_RestoresItsContentAndTouchesItInThePolicy()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        fixture.Views[tabs[0]].Released = true;
        fixture.Active = tabs[0];

        // Act
        sut.Activated(tabs[0]);

        // Assert
        fixture.Views[tabs[0]].View.Verify(v => v.TryRestoreContentAsync(), Times.Once);
        fixture.Policy.Verify(p => p.Touch(tabs[0]), Times.Exactly(2)); // when it was opened, and now
        Assert.True(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void Activated_LoadedTab_DoesNotRestoreAnything()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");

        // Act
        sut.Activated(tabs[0]);

        // Assert
        fixture.Views[tabs[0]].View.Verify(v => v.TryRestoreContentAsync(), Times.Never);
    }

    [Fact]
    public void Activated_ContentCannotBeRead_ReportsItAndTheTabStaysReleasedForTheNextTry()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        var view = fixture.Views[tabs[0]];
        view.Released = true;
        view.CanRestore = false;
        fixture.Active = tabs[0];

        // Act
        sut.Activated(tabs[0]);
        var afterTheFailure = sut.IsContentLoaded(tabs[0]);
        view.CanRestore = true;
        sut.Activated(tabs[0]);

        // Assert
        Assert.Equal([tabs[0]], fixture.NotRestored);
        Assert.False(afterTheFailure);
        Assert.True(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public async Task EnsureContentAsync_ReleasedTab_RestoresItAndCountsItAsUsed()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        fixture.Views[tabs[0]].Released = true;

        // Act
        await sut.EnsureContentAsync(tabs[0]).WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sut.IsContentLoaded(tabs[0]));
        fixture.Policy.Verify(p => p.Touch(tabs[0]), Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureContentAsync_ARestoreIsUnderway_JoinsItInsteadOfStartingAnother()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        var view = fixture.Views[tabs[0]];
        view.Released = true;
        view.RestoreGate = new TaskCompletionSource<bool>();
        fixture.Active = tabs[0];
        sut.Activated(tabs[0]); // selecting the tab starts reading it

        // Act
        var saving = sut.EnsureContentAsync(tabs[0]); // and saving it asks for the text before it is back
        view.RestoreGate.SetResult(true);
        await saving.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        view.View.Verify(v => v.TryRestoreContentAsync(), Times.Once);
        Assert.True(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public async Task WhenIdleAsync_ARestoreIsUnderway_CompletesWhenItDoes()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        var view = fixture.Views[tabs[0]];
        view.Released = true;
        view.RestoreGate = new TaskCompletionSource<bool>();
        fixture.Active = tabs[0];
        sut.Activated(tabs[0]);

        // Act
        var idle = sut.WhenIdleAsync();
        var completedEarly = idle.IsCompleted;
        view.RestoreGate.SetResult(true);
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(completedEarly);
        Assert.True(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public async Task WhenIdleAsync_NothingIsBeingRestored_IsAlreadyComplete()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        Open(fixture, sut, "a", "b");

        // Act
        await sut.WhenIdleAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sut.WhenIdleAsync().IsCompleted);
    }

    [Fact]
    public async Task EnsureContentAsync_ClosedTab_DoesNothing()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a", "b");
        fixture.Views[tabs[0]].Released = true;
        sut.Unregister(tabs[0]);

        // Act
        await sut.EnsureContentAsync(tabs[0]).WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        fixture.Views[tabs[0]].View.Verify(v => v.TryRestoreContentAsync(), Times.Never);
        Assert.False(sut.IsContentLoaded(tabs[0]));
    }

    [Fact]
    public void Unregister_Tab_ForgetsItInThePolicy()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        var tabs = Open(fixture, sut, "a");

        // Act
        sut.Unregister(tabs[0]);

        // Assert
        fixture.Policy.Verify(p => p.Forget(tabs[0]), Times.Once);
    }

    [Fact]
    public void Detach_Called_DeletesWhatWasSpilled()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();
        Open(fixture, sut, "a", "b");

        // Act
        sut.Detach();

        // Assert
        fixture.Spill.Verify(s => s.DeleteAll(), Times.Once);
    }

    [Fact]
    public void IsContentLoaded_UnknownTab_IsFalse()
    {
        // Arrange
        var fixture = new TabLifecycleManagerFixture();
        var sut = fixture.CreateSut();

        // Act
        var loaded = sut.IsContentLoaded(fixture.CreateTab("never registered"));

        // Assert
        Assert.False(loaded);
    }
}
