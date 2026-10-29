namespace Invoklet.Avalonia.CodeTextEditor;

public class Breakpoint : IBreakpoint
{
    public int Line { get; set; }
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
    public BreakpointVerificationState VerificationState { get; set; } = BreakpointVerificationState.Pending;
    public string? VerificationMessage { get; set; }
    public bool IsEnabled { get; set; } = true;
}
