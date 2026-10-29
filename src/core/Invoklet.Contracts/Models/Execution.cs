namespace Invoklet.Contracts.Models;

public enum OutputKind
{
    StandardOutput,
    StandardError,
    DumpHtml,

    /// <summary>
    /// Standard output text that ends without a line terminator (<c>Console.Write</c>): the next
    /// standard output continues the same line. <see cref="StandardOutput"/> is a complete line.
    /// </summary>
    StandardOutputPartial,

    /// <summary>Like <see cref="StandardOutputPartial"/>, for standard error.</summary>
    StandardErrorPartial,

    /// <summary>
    /// A compiler warning or message (errors are <see cref="StandardError"/>). Not script output, so a
    /// runner that compares or pipes the script's stdout keeps it apart.
    /// </summary>
    Diagnostic,
}

public delegate void OutputReceivedHandler(string data, OutputKind kind);

/// <summary>How a <see cref="Invoklet.Contracts.ILanguageHost.RunAsync"/> call ended — see <see cref="RunResult"/>.</summary>
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
/// The result of <see cref="Invoklet.Contracts.ILanguageHost.RunAsync"/>: success is decided by
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

/// <summary>Raised after a successful compile with the path to the compiled assembly on disk — see <see cref="Invoklet.Contracts.ILanguageHost.Compiled"/>.</summary>
public delegate void CompiledHandler(string binaryPath);

/// <summary>Raised when a running script calls Util.ReadLine, or Util.GetSecret asks for a missing secret — see <see cref="Invoklet.Contracts.ILanguageHost.InputRequested"/>.</summary>
public delegate void InputRequestedHandler(string? prompt, bool isPassword);

/// <summary>The stage of preparing and running a script that a host reports through <see cref="Invoklet.Contracts.ILanguageHost.StageChanged"/>.</summary>
public enum RunStage
{
    /// <summary>Reading the schemas of the script's connections (done by the caller before the host is asked to run).</summary>
    ReadingConnectionSchemas,

    /// <summary>Downloading and restoring NuGet packages (and the targeting pack, if it has to be downloaded).</summary>
    RestoringPackages,

    /// <summary>Compiling the connections' generated classes into the contract the script is built against.</summary>
    GeneratingConnections,

    /// <summary>Compiling the script itself.</summary>
    Compiling,

    /// <summary>The compiled script is running (or being debugged).</summary>
    Running,
}

/// <summary>
/// Raised when a host moves on to another <see cref="RunStage"/>, with an optional detail such as what
/// is being restored. Raised on whichever thread the host works on.
/// </summary>
public delegate void StageChangedHandler(RunStage stage, string? detail);
