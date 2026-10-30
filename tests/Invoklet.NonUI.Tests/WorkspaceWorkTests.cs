using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class WorkspaceWorkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitUntilIdleAsync_NothingIsGoingOn_ReturnsTheIncludedTabs()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var kept = new WorkspaceWorkFixture.FakeTab("kept").AlreadyReady();
        var left = new WorkspaceWorkFixture.FakeTab("left").AlreadyReady();
        fixture.Tabs.AddRange([kept, left]);
        var sut = fixture.CreateSut();

        // Act
        var tabs = await fixture.WaitUntilIdle(sut, t => t != left).WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([kept], tabs);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_ASaveIsGoingOn_WaitsForIt()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var gate = WorkspaceWorkFixture.NewGate();
        fixture.Tabs.Add(new WorkspaceWorkFixture.FakeTab("a") { PendingSave = gate.Task }.AlreadyReady());
        var sut = fixture.CreateSut();

        // Act
        var idle = fixture.WaitUntilIdle(sut);
        var doneEarly = idle.IsCompleted;
        gate.SetResult();
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneEarly);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_ASaveAsIsGoingOn_WaitsForItToo()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var gate = WorkspaceWorkFixture.NewGate();
        fixture.Tabs.Add(new WorkspaceWorkFixture.FakeTab("a") { PendingSaveAs = gate.Task }.AlreadyReady());
        var sut = fixture.CreateSut();

        // Act
        var idle = fixture.WaitUntilIdle(sut);
        var doneEarly = idle.IsCompleted;
        gate.SetResult();
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneEarly);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_TheSaveOfAnExcludedTabIsGoingOn_StillWaitsForIt()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var gate = WorkspaceWorkFixture.NewGate();
        var excluded = new WorkspaceWorkFixture.FakeTab("excluded") { PendingSave = gate.Task };
        fixture.Tabs.AddRange([excluded, new WorkspaceWorkFixture.FakeTab("other").AlreadyReady()]);
        var sut = fixture.CreateSut();

        // Act
        var idle = fixture.WaitUntilIdle(sut, t => t != excluded);
        var doneEarly = idle.IsCompleted;
        gate.SetResult();
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneEarly);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_ATrackedOperationIsGoingOn_WaitsForIt()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var gate = WorkspaceWorkFixture.NewGate();
        var sut = fixture.CreateSut();
        sut.Track(() => gate.Task);

        // Act
        var idle = fixture.WaitUntilIdle(sut);
        var doneEarly = idle.IsCompleted;
        gate.SetResult();
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneEarly);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_AnotherSaveBeginsWhileWaiting_WaitsForThatOneToo()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var first = WorkspaceWorkFixture.NewGate();
        var second = WorkspaceWorkFixture.NewGate();
        var tab = new WorkspaceWorkFixture.FakeTab("a") { PendingSave = first.Task }.AlreadyReady();
        fixture.Tabs.Add(tab);
        var sut = fixture.CreateSut();
        var idle = fixture.WaitUntilIdle(sut);

        // Act
        tab.PendingSaveAs = second.Task; // begins after the wait began
        first.SetResult();
        await Task.Yield();
        var doneAfterTheFirst = idle.IsCompleted;
        second.SetResult();
        await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneAfterTheFirst);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_ATabIsNotReadyInTime_Throws()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        fixture.Tabs.Add(new WorkspaceWorkFixture.FakeTab("slow")); // its editor never gets ready
        var sut = fixture.CreateSut(readyTimeout: TimeSpan.FromMilliseconds(30));

        // Act
        var wait = () => fixture.WaitUntilIdle(sut);

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(wait);
    }

    [Fact]
    public async Task WaitUntilIdleAsync_ATabIsAddedWhileWaiting_IsIncludedInTheResult()
    {
        // Arrange
        var fixture = new WorkspaceWorkFixture();
        var gate = WorkspaceWorkFixture.NewGate();
        fixture.Tabs.Add(new WorkspaceWorkFixture.FakeTab("a") { PendingSave = gate.Task }.AlreadyReady());
        var sut = fixture.CreateSut();
        var idle = fixture.WaitUntilIdle(sut);
        var added = new WorkspaceWorkFixture.FakeTab("added").AlreadyReady();

        // Act
        fixture.Tabs.Add(added);
        gate.SetResult();
        var tabs = await idle.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(added, tabs);
    }
}
