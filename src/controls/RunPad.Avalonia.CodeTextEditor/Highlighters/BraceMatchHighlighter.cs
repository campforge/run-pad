using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

internal sealed class BraceMatchHighlighter : IBackgroundRenderer
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(65, 100, 120, 220));
    private static readonly IBrush MismatchBrush = new SolidColorBrush(Color.FromArgb(80, 220, 60, 60));

    private int _openPos = -1;
    private int _closePos = -1;
    private bool _matched;

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Supply matched pair positions; both -1 to clear.</summary>
    public void Update(int openPos, int closePos, bool matched)
    {
        _openPos = openPos;
        _closePos = closePos;
        _matched = matched;
    }

    public void Clear() => (_openPos, _closePos) = (-1, -1);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if ((_openPos < 0 && _closePos < 0) || textView.Document is null) return;
        var brush = _matched ? MatchBrush : MismatchBrush;
        DrawAt(textView, drawingContext, _openPos, brush);
        DrawAt(textView, drawingContext, _closePos, brush);
    }

    private static void DrawAt(TextView textView, DrawingContext ctx, int offset, IBrush brush)
    {
        if (offset < 0 || offset >= textView.Document.TextLength) return;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
        builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + 1 });
        var geo = builder.CreateGeometry();
        if (geo != null) ctx.DrawGeometry(brush, null, geo);
    }
}
