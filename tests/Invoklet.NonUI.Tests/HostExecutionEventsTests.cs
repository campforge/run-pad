using Invoklet.Contracts.Models;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class HostExecutionEventsTests
{
    [Fact]
    public void Events_PreserveCompiledStageAndOutputPayloads()
    {
        HostExecutionEvent compiled = new HostExecutionEvent.Compiled("out/app.dll");
        HostExecutionEvent stage = new HostExecutionEvent.Stage(RunStage.Compiling, "C#");
        HostExecutionEvent output = new HostExecutionEvent.Output("hello", OutputKind.StandardOutput);

        Assert.Equal("out/app.dll", Assert.IsType<HostExecutionEvent.Compiled>(compiled).BinaryPath);
        Assert.Equal(RunStage.Compiling, Assert.IsType<HostExecutionEvent.Stage>(stage).Value);
        Assert.Equal("hello", Assert.IsType<HostExecutionEvent.Output>(output).Data);
    }

    [Fact]
    public void Result_SeparatesSuccessCancellationAndError()
    {
        Assert.True(HostExecutionResult.Completed(RunResult.Exited(0)).Succeeded);
        Assert.False(HostExecutionResult.Canceled().Succeeded);
        Assert.False(HostExecutionResult.Failed(new InvalidOperationException()).Succeeded);
    }
}
