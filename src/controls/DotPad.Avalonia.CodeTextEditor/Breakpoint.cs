namespace DotPad.Avalonia.CodeTextEditor;

public class Breakpoint : IBreakpoint
{
    public string FileName { get; } = string.Empty;
    public int Line { get; set; }
    public string? Condition { get; set; }
    public BreakpointVerificationState VerificationState { get; set; } = BreakpointVerificationState.Pending;
    public string? VerificationMessage { get; set; }
}
