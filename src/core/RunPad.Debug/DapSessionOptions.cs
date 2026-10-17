using RunPad.Contracts;

namespace RunPad.Debug;

/// <summary>
/// Plain per-session configuration for <see cref="DapSession"/> — split out of its constructor
/// (was 13 positional parameters) so callbacks/dependencies (process, handlers,
/// logger) stay separate from what's really just data about the run being debugged.
/// </summary>
/// <param name="ProgramPath">Path to the compiled assembly netcoredbg should launch.</param>
/// <param name="WorkingDirectory">Working directory for the debuggee.</param>
/// <param name="Breakpoints">Breakpoints to arm before the debuggee starts running.</param>
/// <param name="SourceFileExtension">e.g. <c>.cs</c>/<c>.vb</c> — used for breakpoint path matching against the compiled source file.</param>
/// <param name="LineOffset">Lines prepended by the compiler/kind-transform before the user's own first line — see <see cref="DapSession"/>'s own field doc.</param>
/// <param name="BreakOnAllExceptions">DAP <c>setExceptionBreakpoints</c> — break on every thrown exception, not just uncaught ones.</param>
/// <param name="BreakOnUncaughtExceptions">DAP <c>setExceptionBreakpoints</c> — break on exceptions that would otherwise crash the debuggee.</param>
/// <param name="JustMyCode">netcoredbg's own "Just My Code" launch setting.</param>
/// <param name="CurrentQueryPath">The .rpad file's own path, forwarded to the
/// debuggee via the launch request's own <c>env</c> (RunPad.Runtime.Util.CurrentQueryPath's
/// RunAsync-only env var, previously never reaching a debug session at all — netcoredbg spawns the
/// debuggee itself, so ProcessStartInfo.Environment on netcoredbg's own process has no effect on
/// it). <see langword="null"/> for a never-saved (untitled) tab, same as the non-debug run.</param>
/// <param name="RuntimePath">The <c>dotnet</c> host that runs <paramref name="ProgramPath"/> — the same
/// one the non-debug run uses. When set, the launch request names it as <c>program</c> with the
/// assembly as its first <c>args</c> entry; otherwise netcoredbg turns a bare <c>.dll</c> program into
/// a PATH lookup of <c>dotnet</c>, which fails (0x80070002) for a macOS GUI app whose launchd PATH
/// doesn't include the SDK directory. <see langword="null"/>/empty keeps that legacy behavior.</param>
/// <param name="ExceptionInfoTimeout">How long an exception stop waits for the optional
/// <c>exceptionInfo</c> details before it is shown with only the stop's own text;
/// <see langword="null"/> uses the session's default.</param>
/// <param name="Arguments">The script's command-line arguments (its <c>args</c>), passed after the
/// assembly in the launch request's <c>args</c>.</param>
public sealed record DapSessionOptions(
    string ProgramPath,
    string WorkingDirectory,
    IReadOnlyList<IBreakpoint> Breakpoints,
    string SourceFileExtension,
    int LineOffset,
    bool BreakOnAllExceptions = false,
    bool BreakOnUncaughtExceptions = true,
    bool JustMyCode = false,
    string? CurrentQueryPath = null,
    string? RuntimePath = null,
    TimeSpan? ExceptionInfoTimeout = null,
    IReadOnlyList<string>? Arguments = null);
