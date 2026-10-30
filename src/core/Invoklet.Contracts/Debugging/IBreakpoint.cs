namespace Invoklet.Contracts.Debugging;

public interface IBreakpoint
{
    string FileName { get; }
    int Line { get; set; }
    string? Condition { get; }

    // DAP SourceBreakpoint.hitCondition — an expression like "= 5"/">= 3"/"% 2" evaluated against
    // the breakpoint's hit count (not the program state, unlike Condition).
    string? HitCondition { get; }
}
