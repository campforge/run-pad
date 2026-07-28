using RunPad.Contracts;

namespace RunPad.Debug;

/// <summary>
/// Plain per-session configuration for <see cref="DapSession"/> — split out of its constructor
/// (ANALYSIS.md #88, was 13 positional parameters) so callbacks/dependencies (process, handlers,
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
/// <param name="CurrentQueryPath">TODO.md #111 — the .rpad file's own path, forwarded to the
/// debuggee via the launch request's own <c>env</c> (RunPad.Runtime.Util.CurrentQueryPath's
/// RunAsync-only env var, previously never reaching a debug session at all — netcoredbg spawns the
/// debuggee itself, so ProcessStartInfo.Environment on netcoredbg's own process has no effect on
/// it). <see langword="null"/> for a never-saved (untitled) tab, same as the non-debug run.</param>
public sealed record DapSessionOptions(
    string ProgramPath,
    string WorkingDirectory,
    IReadOnlyList<IBreakpoint> Breakpoints,
    string SourceFileExtension,
    int LineOffset,
    bool BreakOnAllExceptions = false,
    bool BreakOnUncaughtExceptions = true,
    bool JustMyCode = false,
    string? CurrentQueryPath = null);
