
using AvaloniaEdit.Highlighting;

namespace DotPad.Avalonia.CodeTextEditor.Shared;

public interface IClassificationHighlightColors
{
    HighlightingColor DefaultBrush { get; }

    HighlightingColor GetBrush(string classificationTypeName);
}
