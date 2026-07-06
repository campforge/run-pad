using DotPad.Contracts.Models;

namespace DotPad.Contracts;

public interface ILanguageHost : IAsyncDisposable
{
    string Language { get; }
    LanguageHostCapabilities Capabilities { get; }
    bool Supports(LanguageHostCapabilities capability);

    // Always available: compile, run, stop.
    Task<ICompilationResult> CompileAsync(string code, CancellationToken cancellationToken);

    Task RunAsync(string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken);

    Task TerminateAsync(CancellationToken cancellationToken);

    // Requires Debug capability.
    Task RunDebugAsync(
        string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    // Requires Breakpoints capability.
    void AddBreakpoint(IBreakpoint breakpoint);
    void RemoveBreakpoint(IBreakpoint breakpoint);
    void ClearBreakpoints();
    event BreakpointHitHandler? BreakpointHit;
    event BreakpointVerifiedHandler? BreakpointVerified;

    // Requires MemoryDump capability.
    Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken);
}
