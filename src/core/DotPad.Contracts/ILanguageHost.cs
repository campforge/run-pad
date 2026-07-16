using DotPad.Contracts.Models;

namespace DotPad.Contracts;

/// <summary>
/// One running instance of a language backend (compile/run/debug for a single script execution).
/// Created by an <see cref="ILanguageHostFactory"/> — see <see cref="ILanguageHostRegistry"/> for
/// how a language id (e.g. <c>"csharp"</c>) resolves to a factory. Methods gated on a capability
/// (see the comments below) throw if that capability isn't in <see cref="Capabilities"/> — check
/// with <see cref="Supports"/> first, or call <see cref="ILanguageHostRegistry.GetCapabilities"/>
/// before creating the host at all.
/// </summary>
public interface ILanguageHost : IAsyncDisposable
{
    /// <summary>This host's language id — matches the <see cref="ILanguageHostFactory"/> that created it.</summary>
    string Language { get; }

    /// <summary>Which optional operations below this host actually supports.</summary>
    LanguageHostCapabilities Capabilities { get; }

    /// <summary>Shorthand for <c>(Capabilities &amp; capability) == capability</c>.</summary>
    bool Supports(LanguageHostCapabilities capability);

    /// <summary>Compiles <paramref name="code"/> without running it. Always available.</summary>
    Task<ICompilationResult> CompileAsync(string code, CancellationToken cancellationToken);

    /// <summary>Compiles and runs <paramref name="code"/>, streaming stdout/stderr/dump output to <paramref name="outputHandler"/>. Always available.</summary>
    Task RunAsync(string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken);

    /// <summary>Stops the current run, if any. Always available.</summary>
    Task TerminateAsync(CancellationToken cancellationToken);

    /// <summary>Like <see cref="RunAsync"/>, but attaches a debugger and honors breakpoints. Requires <see cref="LanguageHostCapabilities.Debug"/>.</summary>
    Task RunDebugAsync(
        string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken);

    /// <summary>Pauses a running debug session. Requires <see cref="LanguageHostCapabilities.Debug"/>.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Adds a breakpoint to the next/current debug session. Requires <see cref="LanguageHostCapabilities.Breakpoints"/>.</summary>
    void AddBreakpoint(IBreakpoint breakpoint);

    /// <summary>Removes a previously added breakpoint. Requires <see cref="LanguageHostCapabilities.Breakpoints"/>.</summary>
    void RemoveBreakpoint(IBreakpoint breakpoint);

    /// <summary>Removes every breakpoint added so far. Requires <see cref="LanguageHostCapabilities.Breakpoints"/>.</summary>
    void ClearBreakpoints();

    /// <summary>Raised when execution stops at a breakpoint. Requires <see cref="LanguageHostCapabilities.Breakpoints"/>.</summary>
    event BreakpointHitHandler? BreakpointHit;

    /// <summary>Raised once the debugger confirms whether a requested breakpoint actually bound to a location. Requires <see cref="LanguageHostCapabilities.Breakpoints"/>.</summary>
    event BreakpointVerifiedHandler? BreakpointVerified;

    /// <summary>
    /// Snapshots the local variables from the most recent breakpoint hit (not a full managed-heap
    /// dump). Requires <see cref="LanguageHostCapabilities.MemoryDump"/>.
    /// </summary>
    Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken);
}
