namespace DotPad.Avalonia.CodeTextEditor;

public class Breakpoint : IBreakpoint
{
    public string FileName { get; }
    public int Line { get; set; }
}