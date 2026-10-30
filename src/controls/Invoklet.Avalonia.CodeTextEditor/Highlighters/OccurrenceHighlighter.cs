using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Invoklet.Avalonia.CodeTextEditor;

// Draws a highlight rectangle over every occurrence of the
// symbol under the caret, elsewhere in the document. Same IBackgroundRenderer approach as
// BraceMatchHighlighter, generalized from two fixed single-character positions to an arbitrary list
// of (offset, length) spans.
internal sealed class OccurrenceHighlighter : IBackgroundRenderer
{
    private static readonly IBrush DefaultHighlightBrush = new SolidColorBrush(Color.FromArgb(60, 160, 160, 160));
    public IBrush HighlightBrush { get; set; } = DefaultHighlightBrush;

    // Tracked segments: typing shifts them, so the highlights stay on their words until the next
    // lookup arrives instead of sitting on stale offsets.
    private readonly TrackedSegments<TextSegment> _spans = new();

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Test-only observability — the spans being highlighted, at their current offsets.</summary>
    internal IReadOnlyList<(int Start, int Length)> Spans => [.. _spans.All.Select(s => (s.StartOffset, s.Length))];

    /// <summary>Supply the spans to highlight, following <paramref name="document"/>'s edits; empty to clear.</summary>
    public void Update(TextDocument? document, IReadOnlyList<(int Start, int Length)> spans) =>
        _spans.Replace(document, spans
            .Where(s => s.Start >= 0 && s.Length > 0)
            .OrderBy(s => s.Start)
            .Select(s => new TextSegment { StartOffset = s.Start, Length = s.Length }));

    public void Clear() => _spans.Clear();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_spans.Count == 0 || textView.Document is null) return;

        foreach (var (start, length) in Spans)
        {
            if (start < 0 || length <= 0 || start + length > textView.Document.TextLength) continue;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, EndOffset = start + length });
            var geo = builder.CreateGeometry();
            if (geo != null) drawingContext.DrawGeometry(HighlightBrush, null, geo);
        }
    }
}
