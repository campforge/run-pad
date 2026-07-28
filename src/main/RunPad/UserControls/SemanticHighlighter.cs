using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

// ToDo: Перенести в RunPad.Avalonia.CodeTextEditor
internal sealed class SemanticHighlighter : DocumentColorizingTransformer
{
    private IReadOnlyList<ISemanticToken> _tokens = [];
    private bool _isDark = true;

    public void Update(IReadOnlyList<ISemanticToken> tokens) => _tokens = tokens;
    public void SetDark(bool isDark) => _isDark = isDark;

    protected override void ColorizeLine(DocumentLine line)
    {
        var tokens = _tokens;
        if (tokens.Count == 0) return;

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        foreach (var token in tokens)
        {
            var start = token.StartOffset;
            var end = start + token.Length;
            if (end <= lineStart || start >= lineEnd) continue;

            var brush = SemanticTokenBrushes.GetBrush(token.Kind, _isDark);
            if (brush is null) continue;

            var s = Math.Max(start, lineStart);
            var e = Math.Min(end, lineEnd);
            ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
