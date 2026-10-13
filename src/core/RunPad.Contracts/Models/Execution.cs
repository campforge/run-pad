namespace RunPad.Contracts.Models;

public enum OutputKind
{
    StandardOutput,
    StandardError,
    DumpHtml,
}

public delegate void OutputReceivedHandler(string data, OutputKind kind);

/// <summary>How a <see cref="RunPad.Contracts.ILanguageHost.RunAsync"/> call ended — see <see cref="RunResult"/>.</summary>
public enum RunOutcome
{
    /// <summary>The script didn't compile; its diagnostics were already sent to the output handler.</summary>
    CompilationFailed,

    /// <summary>The compiled script's process couldn't be started.</summary>
    StartFailed,

    /// <summary>The script's process ran and exited — <see cref="RunResult.ExitCode"/> holds its exit code.</summary>
    Exited,
}

/// <summary>
/// The result of <see cref="RunPad.Contracts.ILanguageHost.RunAsync"/> (review R4): success is decided by
/// the process's real exit code, not by whether anything was written to stderr. A cancelled run
/// throws <see cref="OperationCanceledException"/> instead of returning a result.
/// </summary>
public sealed record RunResult(RunOutcome Outcome, int? ExitCode = null)
{
    public static RunResult CompilationFailed { get; } = new(RunOutcome.CompilationFailed);
    public static RunResult StartFailed { get; } = new(RunOutcome.StartFailed);
    public static RunResult Exited(int exitCode) => new(RunOutcome.Exited, exitCode);

    public bool Succeeded => Outcome == RunOutcome.Exited && ExitCode == 0;
}

/// <summary>Raised after a successful compile with the path to the compiled assembly on disk — see <see cref="RunPad.Contracts.ILanguageHost.Compiled"/>.</summary>
public delegate void CompiledHandler(string binaryPath);

/// <summary>Raised when a running script calls Util.ReadLine/GetPassword (TODO.md #63) — see <see cref="RunPad.Contracts.ILanguageHost.InputRequested"/>.</summary>
public delegate void InputRequestedHandler(string? prompt, bool isPassword);
