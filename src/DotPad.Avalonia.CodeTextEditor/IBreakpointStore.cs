using System.Collections.Generic;

namespace DotPad.Avalonia.CodeTextEditor;

public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    void Remove(IBreakpoint bp);
    void Add(int lineClicked);
    
}