namespace Invoklet.Avalonia.CodeTextEditor;

/// <summary>The editor's own gutter breakpoint list — one per open tab, backing the breakpoint margin.</summary>
public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    /// <summary>Raised after a breakpoint is added, so callers (e.g. a running debug session) can forward it downstream.</summary>
    event EventHandler<BreakpointEventArgs>? BreakpointAdded;

    /// <summary>Raised after a breakpoint is removed.</summary>
    event EventHandler<BreakpointEventArgs>? BreakpointRemoved;

    /// <summary>Adds a breakpoint at the given 1-based line, if one doesn't already exist there.</summary>
    void Add(int line);

    /// <summary>Removes a previously added breakpoint.</summary>
    void Remove(IBreakpoint bp);
}
