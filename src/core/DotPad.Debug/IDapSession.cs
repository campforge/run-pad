namespace DotPad.Debug;

/// <summary>
/// A Debug Adapter Protocol session that orchestrates a full debug run.
/// </summary>
public interface IDapSession : IAsyncDisposable
{
    /// <summary>
    /// Runs the full DAP handshake and event loop until the debuggee exits or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Requests a pause of the currently running debuggee.</summary>
    Task PauseAsync(CancellationToken ct);

    /// <summary>
    /// Marks the breakpoint set as stale (a gutter breakpoint was added/removed while this session
    /// is active). The session resends "setBreakpoints" to the debugger the next time execution
    /// naturally stops — sending it immediately isn't safe while the debuggee is running, since that
    /// would race the session's single message-read loop.
    /// </summary>
    void RequestBreakpointResync();

    /// <summary>Flushes any buffered stdout/stderr output to the output handler.</summary>
    void FlushOutputBuffers();
}
