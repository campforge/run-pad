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
}
