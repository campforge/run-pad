using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

// TODO.md #62 "occurrence highlighting" — draws a highlight rectangle over every occurrence of the
// symbol under the caret, elsewhere in the document. Same IBackgroundRenderer approach as
// BraceMatchHighlighter, generalized from two fixed single-character positions to an arbitrary list
// of (offset, length) spans.
internal sealed class OccurrenceHighlighter : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(60, 160, 160, 160));

    private IReadOnlyList<(int Start, int Length)> _spans = [];

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Test-only observability — the last spans handed to <see cref="Update"/>.</summary>
    internal IReadOnlyList<(int Start, int Length)> Spans => _spans;

    /// <summary>Supply the spans to highlight; empty to clear.</summary>
    public void Update(IReadOnlyList<(int Start, int Length)> spans) => _spans = spans;

    public void Clear() => _spans = [];

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_spans.Count == 0 || textView.Document is null) return;

        foreach (var (start, length) in _spans)
        {
            if (start < 0 || length <= 0 || start + length > textView.Document.TextLength) continue;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, EndOffset = start + length });
            var geo = builder.CreateGeometry();
            if (geo != null) drawingContext.DrawGeometry(HighlightBrush, null, geo);
        }
    }
}
