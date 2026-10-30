using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class RunLifecycleTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Start_NoRunIsBusy_StartsTheBodyAndTracksItUntilItEnds()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var run = fixture.NewRun();
        var started = 0;

        // Act
        var task = sut.Start(() => { started++; return run.Task; });
        var busyWhileRunning = sut.IsBusy;
        run.SetResult();
        await task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, started);
        Assert.True(busyWhileRunning);
        Assert.False(sut.IsBusy);
    }

    [Fact]
    public void Start_ARunIsStillBusy_DoesNotStartAnotherOne()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        _ = sut.Start(() => fixture.NewRun().Task);
        var started = 0;

        // Act
        var second = sut.Start(() => { started++; return Task.CompletedTask; });

        // Assert
        Assert.Equal(0, started);
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Start_ThePreviousRunHasEnded_StartsAgain()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var first = fixture.NewRun();
        var task = sut.Start(() => first.Task);
        first.SetResult();
        await task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        var started = 0;

        // Act
        await sut.Start(() => { started++; return Task.CompletedTask; }).WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, started);
    }

    [Fact]
    public void CanStartSelection_NothingRuns_IsTrue()
    {
        var sut = new RunLifecycleFixture().CreateSut();

        Assert.True(sut.CanStartSelection);
    }

    [Fact]
    public void CanStartSelection_ARunIsBusy_IsFalse()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        _ = sut.Start(() => fixture.NewRun().Task);

        // Act
        var can = sut.CanStartSelection;

        // Assert
        Assert.False(can);
    }

    [Fact]
    public async Task CanStartSelection_ARestartIsUnderway_IsFalse()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var next = fixture.NewRun();
        var restart = sut.RestartAsync(() => { }, () => { }, () => next.Task);

        // Act
        var duringRestart = sut.CanStartSelection;
        next.SetResult();
        await restart.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(duringRestart);
        Assert.True(sut.CanStartSelection);
    }

    [Fact]
    public void Begin_Called_MarksTheRunInProgressWithANewGeneration()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var before = sut.RunGeneration;

        // Act
        var generation = sut.Begin();

        // Assert
        Assert.True(sut.InProgress);
        Assert.Equal(before + 1, generation);
        Assert.True(sut.IsCurrent(generation));
    }

    [Fact]
    public void CanBegin_ARunIsInProgress_IsFalseUntilItEnds()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        sut.Begin();

        // Act
        var whileRunning = sut.CanBegin;
        sut.End();

        // Assert
        Assert.False(whileRunning);
        Assert.True(sut.CanBegin);
    }

    [Fact]
    public void CanBegin_TheRunWaitsForItsSave_IsFalseUntilTheSaveEnds()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        sut.BeginSaving();

        // Act
        var whileSaving = sut.CanBegin;
        sut.EndSaving();

        // Assert
        Assert.False(whileSaving);
        Assert.True(sut.CanBegin);
    }

    [Fact]
    public void Stop_ARunBegan_MakesItsGenerationStale()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var generation = sut.Begin();

        // Act
        sut.Stop();

        // Assert
        Assert.False(sut.IsCurrent(generation));
    }

    [Fact]
    public async Task IsCurrent_StoppedOnTheUiThreadWhileAHostThreadSpins_BecomesFalseForIt()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var generation = sut.Begin();
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawTheStop = false;
        var reader = RunLifecycleFixture.RunOnAnotherThread(() =>
        {
            var giveUpAt = RunLifecycleFixture.SpinDeadline();
            spinning.SetResult();
            // As a host callback asking whether its run still counts. It ends by itself at the deadline, so a
            // failed test never leaves a thread running.
            while (sut.IsCurrent(generation))
            {
                if (Environment.TickCount64 > giveUpAt) return;
            }
            sawTheStop = true;
        });
        await spinning.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        sut.Stop();

        // Assert
        await reader.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(sawTheStop); // it saw the stop, however the code was compiled
        Assert.False(sut.IsCurrent(generation));
    }

    [Fact]
    public async Task RunGeneration_RunsBeginAndStopWhileAnotherThreadReads_NeverGoesBackwards()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var done = false;
        var wentBackwards = false;
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = RunLifecycleFixture.RunOnAnotherThread(() =>
        {
            var giveUpAt = RunLifecycleFixture.SpinDeadline();
            var last = sut.RunGeneration;
            reading.SetResult();
            while (!Volatile.Read(ref done) && Environment.TickCount64 <= giveUpAt)
            {
                var now = sut.RunGeneration;
                if (now < last) wentBackwards = true;
                last = now;
            }
        });

        try
        {
            await reading.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

            // Act
            for (var i = 0; i < 20_000; i++)
            {
                sut.Begin();
                sut.Stop();
                sut.End();
            }
        }
        finally { Volatile.Write(ref done, true); } // also when the test fails: the reader must not outlive it
        await reader.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(wentBackwards);
        Assert.Equal(40_000, sut.RunGeneration); // every Begin and every Stop counted once
    }

    [Fact]
    public void IsStoppedSince_NoStopSinceThen_IsFalse()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        sut.Stop();
        var then = sut.StopGeneration;

        // Act
        var stopped = sut.IsStoppedSince(then);

        // Assert
        Assert.False(stopped);
    }

    [Fact]
    public void IsStoppedSince_AStopCameAfterThen_IsTrue()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var then = sut.StopGeneration;

        // Act
        sut.Stop();

        // Assert
        Assert.True(sut.IsStoppedSince(then));
    }

    [Fact]
    public async Task RestartAsync_NothingRuns_StartsTheNextRunWithoutStopping()
    {
        // Arrange
        var sut = new RunLifecycleFixture().CreateSut();
        var calls = new List<string>();

        // Act
        await sut.RestartAsync(() => calls.Add("stop"), () => calls.Add("waiting"), () => { calls.Add("start"); return Task.CompletedTask; })
            .WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["start"], calls);
        Assert.False(sut.Restarting);
        Assert.False(sut.RestartRequested);
    }

    [Fact]
    public async Task RestartAsync_ARunIsBusy_StopsItWaitsForItAndThenStarts()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var busy = fixture.NewRun();
        _ = sut.Start(() => busy.Task);
        var calls = new List<string>();

        // Act
        var restart = sut.RestartAsync(
            () => { calls.Add("stop"); sut.Stop(); },
            () => calls.Add("waiting"),
            () => { calls.Add("start"); return Task.CompletedTask; });
        var beforeTheRunEnds = calls.ToList();
        busy.SetResult();
        await restart.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["stop", "waiting"], beforeTheRunEnds);
        Assert.Equal(["stop", "waiting", "start"], calls);
    }

    [Fact]
    public async Task RestartAsync_TheUserStopsWhileItWaits_DoesNotStartTheNextRun()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var busy = fixture.NewRun();
        _ = sut.Start(() => busy.Task);
        var started = false;
        var restart = sut.RestartAsync(sut.Stop, () => { }, () => { started = true; return Task.CompletedTask; });

        // Act
        sut.Stop(); // the user presses Stop while the restart waits for the run to end
        busy.SetResult();
        await restart.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(started);
        Assert.False(sut.Restarting);
    }

    [Fact]
    public async Task RestartAsync_AnotherRestartWhileOneIsUnderway_IsIgnored()
    {
        // Arrange
        var fixture = new RunLifecycleFixture();
        var sut = fixture.CreateSut();
        var next = fixture.NewRun();
        var starts = 0;
        var first = sut.RestartAsync(() => { }, () => { }, () => { starts++; return next.Task; });

        // Act
        var second = sut.RestartAsync(() => { }, () => { }, () => { starts++; return Task.CompletedTask; });
        next.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, starts);
    }
}
