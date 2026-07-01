using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;

namespace DotPad.Contracts;

public abstract class LanguageHostBase : ILanguageHost
{
    private readonly List<IBreakpoint> _breakpoints = new();
    private BreakpointHitHandler? _breakpointHit;
    private bool _disposed;

    public abstract string Language { get; }
    public abstract LanguageHostCapabilities Capabilities { get; }

    public bool Supports(LanguageHostCapabilities capability) => Capabilities.HasFlag(capability);

    // --- Core: must be implemented by every host ---

    public abstract Task<ICompilationResult> CompileAsync(string code, CancellationToken cancellationToken = default);
    public abstract Task RunAsync(string code, OutputReceivedHandler? outputHandler = null, CancellationToken cancellationToken = default);
    public abstract Task TerminateAsync(CancellationToken cancellationToken = default);

    // --- Debug: virtual, throws if capability not declared ---

    public virtual Task RunDebugAsync(string code, OutputReceivedHandler? outputHandler = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        return Task.CompletedTask; // unreachable
    }

    public virtual Task PauseAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        return Task.CompletedTask;
    }

    // --- Breakpoints: virtual, maintains internal list ---

    public virtual void AddBreakpoint(IBreakpoint breakpoint)
    {
        EnsureCapability(LanguageHostCapabilities.Breakpoints);
        _breakpoints.Add(breakpoint);
    }

    public virtual void RemoveBreakpoint(IBreakpoint breakpoint)
    {
        EnsureCapability(LanguageHostCapabilities.Breakpoints);
        _breakpoints.Remove(breakpoint);
    }

    public virtual void ClearBreakpoints()
    {
        EnsureCapability(LanguageHostCapabilities.Breakpoints);
        _breakpoints.Clear();
    }

    public virtual event BreakpointHitHandler? BreakpointHit
    {
        add => _breakpointHit += value;
        remove => _breakpointHit -= value;
    }

    // --- Memory dump ---

    public virtual Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability(LanguageHostCapabilities.MemoryDump);
        return Task.FromResult<IMemoryDump>(null!); // unreachable
    }

    // --- IntelliSense ---

    public virtual IIntelliSenseService CreateIntelliSenseService()
    {
        EnsureCapability(LanguageHostCapabilities.IntelliSense);
        return null!; // unreachable
    }

    // --- Helpers for subclasses ---

    protected IReadOnlyList<IBreakpoint> ActiveBreakpoints => _breakpoints.AsReadOnly();

    protected void OnBreakpointHit(IBreakpointHitContext context) => _breakpointHit?.Invoke(context);

    protected void EnsureCapability(LanguageHostCapabilities required)
    {
        if (!Supports(required))
            throw new NotSupportedException(
                $"Language host '{Language}' does not support '{required}'.");
    }

    protected void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    // --- Disposal ---

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
