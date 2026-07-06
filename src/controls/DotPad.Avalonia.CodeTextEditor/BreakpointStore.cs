using System.Collections;

namespace DotPad.Avalonia.CodeTextEditor;

public class BreakpointStore : IBreakpointStore
{
    private readonly List<IBreakpoint> _items = [];

    public event EventHandler<BreakpointEventArgs>? BreakpointAdded;
    public event EventHandler<BreakpointEventArgs>? BreakpointRemoved;

    public IEnumerator<IBreakpoint> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(int line)
    {
        _items.Add(new Breakpoint { Line = line });
        BreakpointAdded?.Invoke(this, new BreakpointEventArgs(line));
    }

    public void Remove(IBreakpoint bp)
    {
        if (!_items.Remove(bp)) return;
        BreakpointRemoved?.Invoke(this, new BreakpointEventArgs(bp.Line));
    }
}
