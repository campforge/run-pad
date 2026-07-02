namespace DotPad.Avalonia.CodeTextEditor;

public class Breakpoint : IBreakpoint
{
    public string FileName { get; } = string.Empty;
    public int Line { get; set; }
}
