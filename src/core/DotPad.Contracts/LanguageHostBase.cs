using DotPad.Contracts.Models;

namespace DotPad.Contracts;

public abstract class LanguageHostBase : ILanguageHost
{
    private readonly List<IBreakpoint> _breakpoints = new();
    private BreakpointHitHandler? _breakpointHit;
    private BreakpointVerifiedHandler? _breakpointVerified;
    private CompiledHandler? _compiled;
    private bool _disposed;

    public abstract string Language { get; }
    public abstract LanguageHostCapabilities Capabilities { get; }

    public bool Supports(LanguageHostCapabilities capability) => Capabilities.HasFlag(capability);

    // --- Core: must be implemented by every host ---

    public abstract Task<ICompilationResult> CompileAsync(string code, CancellationToken cancellationToken);

    public virtual event CompiledHandler? Compiled
    {
        add => _compiled += value;
        remove => _compiled -= value;
    }

    public abstract Task RunAsync(
        string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken);

    public abstract Task TerminateAsync(CancellationToken cancellationToken);

    // --- Debug: virtual, throws if capability not declared ---

    public virtual Task RunDebugAsync(
        string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        // A host that declares the Debug capability but doesn't override this is a genuine
        // implementation bug in that host, not a caller mistake (EnsureCapability above already
        // guards the latter) — silently "succeeding" with a completed task that ran nothing would
        // hide it instead of failing loudly (ANALYSIS.md #94).
        throw new NotImplementedException(
            $"Language host '{Language}' declares Debug capability but does not implement {nameof(RunDebugAsync)}.");
    }

    public virtual Task PauseAsync(CancellationToken cancellationToken)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        throw new NotImplementedException(
            $"Language host '{Language}' declares Debug capability but does not implement {nameof(PauseAsync)}.");
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
        // Match by Line/FileName rather than reference/instance equality: a breakpoint removed from
        // the editor gutter during an active debug session is a freshly-constructed IBreakpoint, not
        // the same instance originally passed to AddBreakpoint.
        _breakpoints.RemoveAll(bp => bp.Line == breakpoint.Line
                                     && (string.IsNullOrEmpty(bp.FileName) || string.IsNullOrEmpty(breakpoint.FileName)
                                                                           || string.Equals(bp.FileName,
                                                                               breakpoint.FileName,
                                                                               StringComparison.OrdinalIgnoreCase)));
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

    public virtual event BreakpointVerifiedHandler? BreakpointVerified
    {
        add => _breakpointVerified += value;
        remove => _breakpointVerified -= value;
    }

    // --- Memory dump ---

    public virtual Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken)
    {
        EnsureCapability(LanguageHostCapabilities.MemoryDump);
        // See RunDebugAsync's identical reasoning (ANALYSIS.md #94) — returning null! here would
        // just relocate the failure to whatever the caller does with the result instead of failing
        // at the actual bug site.
        throw new NotImplementedException(
            $"Language host '{Language}' declares MemoryDump capability but does not implement {nameof(GetMemoryDumpAsync)}.");
    }

    // --- Helpers for subclasses ---

    protected IReadOnlyList<IBreakpoint> ActiveBreakpoints => _breakpoints.AsReadOnly();

    protected void OnBreakpointHit(IBreakpointHitContext context) => _breakpointHit?.Invoke(context);

    protected void OnBreakpointVerified(IBreakpoint breakpoint, bool verified, string? message)
        => _breakpointVerified?.Invoke(breakpoint, verified, message);

    protected void OnCompiled(string binaryPath) => _compiled?.Invoke(binaryPath);

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
