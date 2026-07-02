using System.Collections.Generic;

namespace DotPad.Avalonia.CodeTextEditor;

public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    event EventHandler<BreakpointEventArgs>? BreakpointAdded;
    event EventHandler<BreakpointEventArgs>? BreakpointRemoved;

    void Add(int line);
    void Remove(IBreakpoint bp);
}
