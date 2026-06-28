using System.Collections;
using System.Collections.Generic;

namespace DotPad.Avalonia.CodeTextEditor;

public class BreakpointStore : IBreakpointStore
{
    private List<IBreakpoint> _items = new List<IBreakpoint>();
    public IEnumerator<IBreakpoint> GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Remove(IBreakpoint bp)
    {
        _items.Remove(bp);
    }

    public void Add(int lineClicked)
    {
        _items.Add(new Breakpoint()
        {
            Line = lineClicked
        });
    }
}