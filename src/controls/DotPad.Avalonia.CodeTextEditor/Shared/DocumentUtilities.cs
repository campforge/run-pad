
using AvaloniaEdit.Document;

namespace DotPad.Avalonia.CodeTextEditor.Shared;

internal static class DocumentUtilities
{
    public static int FindPreviousWordStart(this ITextSource textSource, int offset)
    {
        return TextUtilities.GetNextCaretPosition(textSource, offset, LogicalDirection.Backward, CaretPositioningMode.WordStart);
    }
}
