namespace DotPad.Avalonia.CodeTextEditor;

public sealed class CodeTextEditorOptions
{
    public bool ShowBreakpoints { get; set; } = true;
    public IList<int> ColumnRulerPositions { get; set; } = [80, 100];
}
