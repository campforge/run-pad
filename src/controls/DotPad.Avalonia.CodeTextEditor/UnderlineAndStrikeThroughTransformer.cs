using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class UnderlineAndStrikeThroughTransformer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber != 2) return;

        var text = CurrentContext.Document.GetText(line);
        Highlight(line, text, "underline",     TextDecorations.Underline);
        Highlight(line, text, "strikethrough", TextDecorations.Strikethrough);
    }

    private void Highlight(DocumentLine line, string text, string word, TextDecorationCollection decoration)
    {
        int index = text.IndexOf(word, StringComparison.Ordinal);
        if (index == -1) return;

        ChangeLinePart(line.Offset + index, line.Offset + index + word.Length, vl =>
        {
            var existing = vl.TextRunProperties.TextDecorations;
            vl.TextRunProperties.SetTextDecorations(
                existing != null
                    ? new TextDecorationCollection(existing) { decoration[0] }
                    : decoration);
        });
    }
}
