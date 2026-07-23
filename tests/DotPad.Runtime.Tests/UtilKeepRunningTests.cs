using Xunit;

namespace DotPad.Runtime.Tests;

// TODO.md #111 (bucket 1) — LINQPad-inspired Util.KeepRunning. The real effect (a foreground Thread
// that keeps the compiled script's own PROCESS from exiting) can't be observed from inside the single
// long-lived test-runner process these tests already run in, so these assert the actual state machine
// instead, via Util.IsKeepRunningActive (internal, InternalsVisibleTo — same "test hook" pattern as
// ChildProcess.StartInfoForTests). Not [Collection(ConsoleTestCollection.Name)]: doesn't touch
// Console.Out/In. Every test disposes whatever tokens it took out, in a finally, so a failing
// assertion can't leak a live token (and its foreground thread) into later tests in the same run.
public class UtilKeepRunningTests
{
    [Fact]
    public void KeepRunning_Should_ReturnADisposableToken()
    {
        using var token = Util.KeepRunning();

        Assert.NotNull(token);
    }

    [Fact]
    public void KeepRunning_Should_BecomeActive_UntilTheTokenIsDisposed()
    {
        var token = Util.KeepRunning();
        try
        {
            Assert.True(Util.IsKeepRunningActive);
        }
        finally { token.Dispose(); }

        Assert.False(Util.IsKeepRunningActive);
    }

    [Fact]
    public void KeepRunning_Should_StayActive_UntilEveryOutstandingTokenIsDisposed()
    {
        var first = Util.KeepRunning();
        var second = Util.KeepRunning();
        try
        {
            first.Dispose();
            Assert.True(Util.IsKeepRunningActive);

            second.Dispose();
            Assert.False(Util.IsKeepRunningActive);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void Dispose_Should_BeANoOp_WhenCalledTwice()
    {
        // Witnessed via a second, still-outstanding token: if double-disposing "first" wrongly
        // decremented the shared count twice, "second"'s own lease would look released too, even
        // though second.Dispose() was never called — IsKeepRunningActive alone can't tell 0 from -1.
        var first = Util.KeepRunning();
        var second = Util.KeepRunning();
        try
        {
            first.Dispose();
            first.Dispose();

            Assert.True(Util.IsKeepRunningActive);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void IsKeepRunningActive_Should_BeFalse_WhenNoTokenHasEverBeenTaken()
    {
        Assert.False(Util.IsKeepRunningActive);
    }
}
