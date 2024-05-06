namespace DotPad.Avalonia.CodeTextEditor;

public interface IBreakpoint
{
    string FileName { get; }
    int Line { get; set; }
}