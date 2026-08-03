using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

// ToDo: Move to RunPad.Avalonia.CodeTextEditor
internal sealed class SquiggleRenderer : IBackgroundRenderer
{
    private IReadOnlyList<IDiagnosticInfo> _diagnostics = [];

    public KnownLayer Layer => KnownLayer.Background;

    public IReadOnlyList<IDiagnosticInfo> Diagnostics => _diagnostics;

    public void Update(IReadOnlyList<IDiagnosticInfo> diagnostics) => _diagnostics = diagnostics;

    public void Draw(TextView textView, DrawingContext dc)
    {
        var diags = _diagnostics;
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
}
