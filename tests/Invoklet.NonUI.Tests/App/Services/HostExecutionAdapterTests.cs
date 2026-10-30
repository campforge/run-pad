using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class HostExecutionAdapterTests
{
    [Fact]
    public async Task Run_ForwardsOutputStagesAndCompiledEvents()
    {
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Callback<string, OutputReceivedHandler, CancellationToken>((_, output, _) =>
            {
                host.Raise(h => h.StageChanged += null, RunStage.Compiling, "compile");
                host.Raise(h => h.Compiled += null, "/tmp/script.dll");
                output("hello", OutputKind.StandardOutput);
            })
            .ReturnsAsync(RunResult.Exited(0));

        var events = new List<HostExecutionEvent>();
        var result = await HostExecutionAdapter.RunAsync(host.Object, "script", events.Add, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(events, e => e is HostExecutionEvent.Stage { Value: RunStage.Compiling, Detail: "compile" });
        Assert.Contains(events, e => e is HostExecutionEvent.Compiled { BinaryPath: "/tmp/script.dll" });
        Assert.Contains(events, e => e is HostExecutionEvent.Output { Data: "hello", Kind: OutputKind.StandardOutput });
    }

    [Fact]
    public async Task Run_MapsHostError()
    {
        var host = new Mock<ILanguageHost>();
        var expected = new InvalidOperationException("host failed");
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);

        var result = await HostExecutionAdapter.RunAsync(host.Object, "script", _ => { }, CancellationToken.None);

        Assert.Same(expected, result.Error);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Run_ReturningAfterCancellation_IsReportedAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(RunResult.Exited(0));

        var result = await HostExecutionAdapter.RunAsync(host.Object, "script", _ => { }, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task EventsRaisedAfterInvocationCompletes_AreIgnored()
    {
        StageChangedHandler? stage = null;
        InputRequestedHandler? input = null;
        var host = new Mock<ILanguageHost>();
        host.SetupAdd(h => h.StageChanged += It.IsAny<StageChangedHandler>())
            .Callback<StageChangedHandler>(handler => stage = handler);
        host.SetupAdd(h => h.InputRequested += It.IsAny<InputRequestedHandler>())
            .Callback<InputRequestedHandler>(handler => input = handler);
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResult.Exited(0));

        var events = new List<HostExecutionEvent>();
        var result = await HostExecutionAdapter.RunAsync(host.Object, "script", events.Add, CancellationToken.None);
        stage!(RunStage.Running, "late");
        input!("late prompt", false);

        Assert.True(result.Succeeded);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Run_MapsExpectedCancellationAndUnsubscribesHandlers()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var result = await HostExecutionAdapter.RunAsync(host.Object, "script", _ => { }, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Debug_UsesSameEventAndErrorMapping()
    {
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunDebugAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Callback<string, OutputReceivedHandler, CancellationToken>((_, output, _) =>
            {
                host.Raise(h => h.StageChanged += null, RunStage.Running, string.Empty);
                output("debug", OutputKind.StandardOutput);
            })
            .Returns(Task.CompletedTask);

        var events = new List<HostExecutionEvent>();
        var result = await HostExecutionAdapter.DebugAsync(host.Object, "script", events.Add, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(events, e => e is HostExecutionEvent.Stage { Value: RunStage.Running });
        Assert.Contains(events, e => e is HostExecutionEvent.Output { Data: "debug" });
        host.Verify(h => h.RunDebugAsync("script", It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Debug_MapsErrorAndExpectedCancellation()
    {
        var host = new Mock<ILanguageHost>();
        var expected = new InvalidOperationException("debug failed");
        host.Setup(h => h.RunDebugAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        var failed = await HostExecutionAdapter.DebugAsync(host.Object, "script", _ => { }, CancellationToken.None);
        Assert.Same(expected, failed.Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        host.Reset();
        host.Setup(h => h.RunDebugAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException(cts.Token));
        var cancelled = await HostExecutionAdapter.DebugAsync(host.Object, "script", _ => { }, cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.Error);
    }
}
