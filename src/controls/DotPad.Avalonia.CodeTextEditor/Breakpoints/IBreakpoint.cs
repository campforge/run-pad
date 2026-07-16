namespace DotPad.Avalonia.CodeTextEditor;

public interface IBreakpoint
{
    int Line { get; set; }
    string? Condition { get; set; }

    // DAP SourceBreakpoint.hitCondition — an expression like "= 5"/">= 3"/"% 2" evaluated against
    // the breakpoint's hit count (not the program state, unlike Condition).
    string? HitCondition { get; set; }
    BreakpointVerificationState VerificationState { get; set; }
    string? VerificationMessage { get; set; }

    // TODO.md #83: a disabled breakpoint stays in the gutter (contour-only icon) but is left out of
    // the setBreakpoints request built at run start — unlike Remove, it's not forgotten.
    bool IsEnabled { get; set; }
}
