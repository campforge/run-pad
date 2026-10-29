using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.UserControls;

// ToDo: Move to Invoklet.Avalonia.CodeTextEditor
internal sealed class SemanticHighlighter : DocumentColorizingTransformer
{
    // Tracked, not absolute offsets: typing shifts them, so the colors stay on their words until the
    // next analysis instead of landing on the neighbouring characters (the flicker after every key).
    private readonly TrackedSegments<TokenSegment> _tokens = new();
    private bool _isDark = true;

    /// <summary>
    /// Replaces the tokens and returns the span whose coloring actually changed — the part of the
    /// document to redraw — or null when nothing did.
    /// </summary>
    public (int Start, int End)? Update(TextDocument? document, IReadOnlyList<ISemanticToken> tokens)
    {
        var before = _tokens.All;
        var after = tokens
            .OrderBy(t => t.StartOffset)
            .Select(t => new TokenSegment(t.Kind) { StartOffset = t.StartOffset, Length = t.Length })
            .ToList();
        _tokens.Replace(document, after);
        return TrackedSegments<TokenSegment>.ChangedRange(before, _tokens.All, (a, b) => a.Kind == b.Kind);
    }

    public void SetDark(bool isDark) => _isDark = isDark;

    internal IReadOnlyList<TokenSegment> Tokens => _tokens.All;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_tokens.Count == 0) return;

        // only the tokens on this line (a range lookup), not every token of the document per line
        foreach (var token in _tokens.Overlapping(line.Offset, line.Length))
        {
            var brush = SemanticTokenBrushes.GetBrush(token.Kind, _isDark);
            if (brush is null) continue;

            var start = Math.Max(token.StartOffset, line.Offset);
            var end = Math.Min(token.EndOffset, line.EndOffset);
            if (start >= end) continue;
            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    internal sealed class TokenSegment(SemanticTokenKind kind) : TextSegment
    {
        public SemanticTokenKind Kind { get; } = kind;
    }
}
