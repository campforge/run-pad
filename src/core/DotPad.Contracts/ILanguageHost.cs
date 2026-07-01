using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;

namespace DotPad.Contracts;

public interface ILanguageHost : IAsyncDisposable
{
    string Language { get; }
    LanguageHostCapabilities Capabilities { get; }
    bool Supports(LanguageHostCapabilities capability);

    // Always available: compile, run, stop.
    Task<ICompilationResult> CompileAsync(string code, CancellationToken cancellationToken = default);
    Task RunAsync(string code, OutputReceivedHandler? outputHandler = null, CancellationToken cancellationToken = default);
    Task TerminateAsync(CancellationToken cancellationToken = default);

    // Requires Debug capability.
    Task RunDebugAsync(string code, OutputReceivedHandler? outputHandler = null, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);

    // Requires Breakpoints capability.
    void AddBreakpoint(IBreakpoint breakpoint);
    void RemoveBreakpoint(IBreakpoint breakpoint);
    void ClearBreakpoints();
    event BreakpointHitHandler? BreakpointHit;

    // Requires MemoryDump capability.
    Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken = default);

    // Requires IntelliSense capability.
    IIntelliSenseService CreateIntelliSenseService();
}
