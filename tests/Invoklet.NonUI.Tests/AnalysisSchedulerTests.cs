using Invoklet.Languages.CSharp.IntelliSense;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class AnalysisSchedulerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Notify_AfterTheDebounce_AnalysesTheTextTheEditorHasThen()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var analysed = new List<string>();

        // Act
        sut.Notify(() => "var a = 1;", (code, _) =>
        {
            analysed.Add(code);
            return Task.FromResult(true);
        });
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal(["var a = 1;"], analysed);
    }

    [Fact]
    public async Task Notify_SupersededDuringTheDebounce_NeverAsksForItsText()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture { Debounce = TimeSpan.FromHours(1) };
        var sut = fixture.CreateSut();
        var firstAsked = 0;
        var analysed = new List<string>();
        sut.Notify(() => { firstAsked++; return "first"; }, (code, _) => { analysed.Add(code); return Task.FromResult(true); });
        var firstPass = sut.Pass!;

        // Act
        fixture.Debounce = TimeSpan.Zero;
        sut.Notify(() => "second", (code, _) => { analysed.Add(code); return Task.FromResult(true); });
        await AnalysisSchedulerFixture.SettleAsync(sut);
        await firstPass.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, firstAsked);
        Assert.Equal(["second"], analysed);
    }

    [Fact]
    public async Task Notify_ANewerNotificationArrivesDuringAnalysis_CancelsTheTokenOfTheOlderPass()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Notify(() => "old", async (_, token) =>
        {
            started.SetResult(token);
            await release.Task;
            return true;
        });
        var oldToken = await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        sut.Notify(() => "new", (_, _) => Task.FromResult(true));
        release.SetResult();
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.True(oldToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Notify_AnalysisSaysItsResultIsStale_AnalysesTheSameTextAgain()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var analysed = new List<string>();
        var answers = new Queue<bool>([false, true]);

        // Act
        sut.Notify(() => "text", (code, _) =>
        {
            analysed.Add(code);
            return Task.FromResult(answers.Dequeue());
        });
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal(["text", "text"], analysed);
    }

    [Fact]
    public async Task Notify_StaleResultButANewerNotificationHasTakenOver_DoesNotAnalyseTheOldTextAgain()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var analysed = new List<string>();

        // Act
        sut.Notify(() => "old", (code, _) =>
        {
            analysed.Add(code);
            // while this runs, the user types: a newer notification takes over, and this result is stale as well
            sut.Notify(() => "new", (newer, _) =>
            {
                analysed.Add(newer);
                return Task.FromResult(true);
            });
            return Task.FromResult(false);
        });
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal(["old", "new"], analysed);
    }

    [Fact]
    public async Task Notify_AnalysisThrows_ReportsItAndGoesOnWithTheNextNotification()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var analysed = new List<string>();

        // Act
        sut.Notify(() => "bad", (_, _) => throw new InvalidOperationException("boom"));
        await AnalysisSchedulerFixture.SettleAsync(sut);
        sut.Notify(() => "good", (code, _) => { analysed.Add(code); return Task.FromResult(true); });
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal("boom", Assert.Single(fixture.Failures).Message);
        Assert.Equal(["good"], analysed);
    }

    [Fact]
    public async Task Notify_AnalysisIsCancelled_IsNotReportedAsAFailure()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.Notify(() => "text", (_, _) => throw new OperationCanceledException());
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Empty(fixture.Failures);
    }

    [Fact]
    public async Task Stop_PassInFlight_CancelsItAndWaitsUntilItHasEnded()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture();
        var sut = fixture.CreateSut();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;
        sut.Notify(() => "text", async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { ended = true; }
            return true;
        });
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        sut.Stop();

        // Assert
        Assert.True(ended);
    }

    [Fact]
    public async Task Stop_PassThatNeverChecksItsToken_ReturnsAfterTheTimeout()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture { StopTimeout = TimeSpan.FromMilliseconds(50) };
        var sut = fixture.CreateSut();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Notify(() => "text", (_, _) =>
        {
            started.SetResult();
            return never.Task;
        });
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        var stop = Task.Run(sut.Stop, TestContext.Current.CancellationToken);

        // Assert
        await stop.WaitAsync(Limit, TestContext.Current.CancellationToken);
        never.SetResult(true);
    }

    [Fact]
    public void Stop_NothingWasNotified_DoesNothing()
    {
        // Arrange
        var sut = new AnalysisSchedulerFixture().CreateSut();

        // Act
        var stop = sut.Stop;

        // Assert
        Assert.Null(Record.Exception(stop));
    }

    [Fact]
    public async Task Notify_AfterStop_StartsNothing()
    {
        // Arrange
        var sut = new AnalysisSchedulerFixture().CreateSut();
        sut.Stop();
        var analysed = 0;

        // Act
        sut.Notify(() => "text", (_, _) =>
        {
            Interlocked.Increment(ref analysed);
            return Task.FromResult(true);
        });
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal(0, analysed);
    }

    [Fact]
    public async Task Stop_PassThatIgnoresCancellationAndEndsLaterWantingARepeat_DoesNotRunAgain()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture { StopTimeout = TimeSpan.FromMilliseconds(50) };
        var sut = fixture.CreateSut();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysed = 0;
        sut.Notify(() => "text", (_, _) =>
        {
            Interlocked.Increment(ref analysed);
            started.TrySetResult();
            return release.Task;
        });
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await Task.Run(sut.Stop, TestContext.Current.CancellationToken);

        // Act: the late pass says its result is obsolete, which would start a repeat of it
        release.SetResult(false);
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Equal(1, analysed);
    }

    [Fact]
    public async Task Stop_PassThatFailsLater_DoesNotTellTheOwnerWhoIsGone()
    {
        // Arrange
        var fixture = new AnalysisSchedulerFixture { StopTimeout = TimeSpan.FromMilliseconds(50) };
        var sut = fixture.CreateSut();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Notify(() => "text", (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        });
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await Task.Run(sut.Stop, TestContext.Current.CancellationToken);

        // Act
        release.SetException(new InvalidOperationException("workspace disposed"));
        await AnalysisSchedulerFixture.SettleAsync(sut);

        // Assert
        Assert.Empty(fixture.Failures);
    }

    [Fact]
    public async Task Stop_RunnerThatExecutesThePassOnTheCallingThread_IsNotBlockedByTheNotifyThatStartedIt()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new AnalysisScheduler(() => TimeSpan.Zero, work => work(), _ => { }, TimeSpan.FromMilliseconds(50));
        var notify = Task.Run(() => sut.Notify(() => "text", (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        }), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act: the pass is still running inside Notify, which must not hold the scheduler's lock meanwhile
        var stop = Task.Run(sut.Stop, TestContext.Current.CancellationToken);

        // Assert
        await stop.WaitAsync(Limit, TestContext.Current.CancellationToken);
        release.SetResult(true);
        await notify.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Notify_FromInsideAPassOnASynchronousRunner_LeavesTheNewerPassAsTheOneLastStarted()
    {
        // Arrange
        var sut = new AnalysisScheduler(() => TimeSpan.Zero, work => work(), _ => { }, TimeSpan.FromSeconds(5));
        var texts = new List<string>();

        // Act: the first analysis says its result is obsolete, which notifies again from inside the pass
        sut.Notify(() => "text", (code, _) =>
        {
            texts.Add(code);
            return Task.FromResult(texts.Count > 1);
        });
        await sut.Pass!.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["text", "text"], texts);
    }
}
