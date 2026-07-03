namespace DotPad.Build.Core.ProcessRunners;

/// <summary>
/// Minimal lifecycle interface for child processes spawned by DotPad.
/// </summary>
public interface IChildProcess : IAsyncDisposable
{
    /// <summary>Kills the process and its entire process tree synchronously.</summary>
    void Kill();

    /// <summary>Waits until the process exits.</summary>
    Task WaitForExitAsync(CancellationToken ct);
}
