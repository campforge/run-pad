using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Debug;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// DapSession used to depend on the concrete, real-Process-backed ChildProcess, so its
// protocol logic could only ever be exercised against an actual netcoredbg process. Now that it
// depends on IChildProcess, a fake with in-memory streams can drive it directly — starting with the
// one path that was previously impossible to unit-test at all: the "fail fast if the
// debugger process couldn't start" check.
public class DapSessionTests
{
    private sealed class FakeChildProcess : IChildProcess
    {
        public bool StartResult { get; init; } = true;
        public bool Started { get; private set; }
        public bool Killed { get; private set; }

        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Start()
        {
            Started = true;
            return StartResult;
        }

        public void Kill() => Killed = true;

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DapSessionOptions MakeOptions() => new(
        ProgramPath: "out.dll",
        WorkingDirectory: ".",
        Breakpoints: [],
        SourceFileExtension: ".cs",
        LineOffset: 0);

    [Fact]
    public async Task RunAsync_Should_ThrowInvalidOperationException_WhenProcessFailsToStart()
    {
        var process = new FakeChildProcess { StartResult = false };
        await using var session = new DapSession(
            process,
            MakeOptions(),
            outputHandler: null,
            onBreakpointHit: static _ => { },
            logger: NullLogger<DapSession>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(CancellationToken.None));
        Assert.True(process.Started);
        // Never got past the up-front check, so nothing should have been killed/waited on.
        Assert.False(process.Killed);
    }
}
