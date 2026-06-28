namespace DotPad.Contracts;

public interface IBreakpoint
{
    string FileName { get; }
    int Line { get; set; }
}

// Editor-side breakpoint list (gutter display, persisted state).
// Synced to ILanguageHost.AddBreakpoint / RemoveBreakpoint by the caller.
public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    void Add(int lineClicked);
    void Remove(IBreakpoint bp);
}