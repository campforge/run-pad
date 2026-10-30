using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.UserControls;

// ToDo: Move to Invoklet.Avalonia.CodeTextEditor
internal sealed class SquiggleRenderer : IBackgroundRenderer
{
    // Tracked segments, not absolute offsets: typing shifts them, so a squiggle (and the hover tooltip
    // reading Diagnostics) stays under its code until the next analysis.
    private readonly TrackedSegments<DiagnosticSegment> _diagnostics = new();

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The diagnostics at their current, edit-adjusted offsets.</summary>
    public IReadOnlyList<IDiagnosticInfo> Diagnostics => _diagnostics.All;

    public void Update(TextDocument? document, IReadOnlyList<IDiagnosticInfo> diagnostics) =>
        _diagnostics.Replace(document, diagnostics
            .OrderBy(d => d.StartOffset)
            .Select(d => new DiagnosticSegment(d.Message, d.Severity)
            {
                StartOffset = d.StartOffset,
                Length = Math.Max(0, d.EndOffset - d.StartOffset),
            }));

    public void Draw(TextView textView, DrawingContext dc)
    {
        var diags = _diagnostics.All;
        if (diags.Count == 0 || !textView.VisualLinesValid) return;

        var docLen = textView.Document.TextLength;

        foreach (var d in diags)
        {
            if (d.StartOffset >= docLen) continue;

            var start = Math.Max(0, d.StartOffset);
            var end = Math.Min(d.EndOffset, docLen);
            if (start >= end) end = Math.Min(start + 1, docLen);
            if (start >= end) continue;

            var color = d.Severity switch
            {
                DiagnosticSeverity.Error => Color.FromRgb(0xF4, 0x4F, 0x4F),
                DiagnosticSeverity.Warning => Color.FromRgb(0xFF, 0x99, 0x00),
                _ => Color.FromRgb(0x4F, 0x9A, 0xF4),
            };

            var pen = new Pen(new SolidColorBrush(color), 1.5);

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(
                textView, new TextSegment { StartOffset = start, EndOffset = end }))
            {
                DrawWavy(dc, pen, rect);
            }
        }
    }

    private static void DrawWavy(DrawingContext dc, IPen pen, Rect rect)
    {
        var y = rect.Bottom - 0.5;
        var left = rect.Left;
        var right = Math.Max(rect.Right, left + 4);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        ctx.BeginFigure(new Point(left, y), false);
        var goUp = true;
        for (var x = left + 2; x <= right; x += 2)
        {
            ctx.LineTo(new Point(x, goUp ? y - 2 : y));
            goUp = !goUp;
        }
        ctx.EndFigure(false);

        dc.DrawGeometry(null, pen, geometry);
    }

    private sealed class DiagnosticSegment(string message, DiagnosticSeverity severity) : TextSegment, IDiagnosticInfo
    {
        public string Message { get; } = message;
        public DiagnosticSeverity Severity { get; } = severity;
    }
}
