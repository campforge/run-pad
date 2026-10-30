namespace Invoklet.Avalonia.CodeTextEditor.Breakpoints;

public class BreakpointEventArgs(int line) : EventArgs
{
    public int Line { get; } = line;
}
