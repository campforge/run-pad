namespace DotPad.Contracts;

public interface IBreakpoint
{
    string FileName { get; }
    int Line { get; set; }
    string? Condition { get; }

    // DAP SourceBreakpoint.hitCondition — an expression like "= 5"/">= 3"/"% 2" evaluated against
    // the breakpoint's hit count (not the program state, unlike Condition).
    string? HitCondition { get; }
}

// Editor-side breakpoint list (gutter display, persisted state).
// Synced to ILanguageHost.AddBreakpoint / RemoveBreakpoint by the caller.
public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    void Add(int lineClicked);
    void Remove(IBreakpoint bp);
}
