namespace DotPad.Avalonia.CodeTextEditor;

public class BreakpointEventArgs(int line) : EventArgs
{
    public int Line { get; } = line;
}
