namespace RunPad.Build.Core.ProcessRunners;

/// <summary>
/// Lifecycle + stream surface for child processes spawned by RunPad — matches <see cref="ChildProcess"/>
/// exactly, so callers (e.g. <c>DapSession</c>, ANALYSIS.md #88) can depend on this instead of the
/// concrete, real-<see cref="System.Diagnostics.Process"/>-backed type and be tested against a fake
/// with in-memory streams instead of a real dotnet/netcoredbg process.
/// </summary>
public interface IChildProcess : IAsyncDisposable
{
    /// <summary>stdout as a text reader (encoding set via <see cref="System.Diagnostics.ProcessStartInfo"/>).</summary>
    StreamReader StandardOutput { get; }

    /// <summary>stderr as a text reader (encoding set via <see cref="System.Diagnostics.ProcessStartInfo"/>).</summary>
    StreamReader StandardError { get; }

    /// <summary>Raw stdout stream — read binary-framed protocols (e.g. DAP) here.</summary>
    Stream OutputStream { get; }

    /// <summary>Raw stdin stream — write binary-framed protocols (e.g. DAP) here.</summary>
    Stream InputStream { get; }

    /// <summary>Starts the process. Returns false if the process could not be started.</summary>
    bool Start();

    /// <summary>Kills the process and its entire process tree synchronously.</summary>
    void Kill();

    /// <summary>Waits until the process exits.</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>The process's exit code — valid once <see cref="WaitForExitAsync"/> has completed.</summary>
    int ExitCode { get; }
}
