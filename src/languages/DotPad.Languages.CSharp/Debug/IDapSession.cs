namespace DotPad.Languages.CSharp.Debug;

/// <summary>
/// A Debug Adapter Protocol session that orchestrates a full debug run.
/// </summary>
internal interface IDapSession : IAsyncDisposable
{
    /// <summary>
    /// Runs the full DAP handshake and event loop until the debuggee exits or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Requests a pause of the currently running debuggee.</summary>
    Task PauseAsync(CancellationToken ct);

    /// <summary>Flushes any buffered stdout/stderr output to the output handler.</summary>
    void FlushOutputBuffers();
}
