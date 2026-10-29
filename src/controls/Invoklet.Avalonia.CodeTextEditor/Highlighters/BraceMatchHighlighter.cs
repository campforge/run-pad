using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Invoklet.Avalonia.CodeTextEditor;

// The brace pair at the caret: a subtle background behind both braces, and — when they are on
// different lines — the scope guide between them emphasized, as in Rider. Colors come from the theme
// (Invoklet.Editor.BraceMatchBrush, BraceMismatchBrush, ActiveScopeGuideBrush); the constants below
// are only the fallback without a Invoklet theme.
internal sealed class BraceMatchHighlighter : IBackgroundRenderer
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(65, 100, 120, 220));
    private static readonly IBrush MismatchBrush = new SolidColorBrush(Color.FromArgb(80, 220, 60, 60));
    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.FromArgb(160, 142, 107, 184));

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

    // Test-only (InternalsVisibleTo): the guide drawn by the last Draw, in text view coordinates.
    internal (Point Top, Point Bottom)? LastGuide { get; private set; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        LastGuide = null;
        if ((_openPos < 0 && _closePos < 0) || textView.Document is null) return;
        var brush = _matched
            ? GutterMetrics.ThemedResource<IBrush>("Invoklet.Editor.BraceMatchBrush") ?? MatchBrush
            : GutterMetrics.ThemedResource<IBrush>("Invoklet.Editor.BraceMismatchBrush") ?? MismatchBrush;
        DrawAt(textView, drawingContext, _openPos, brush);
        DrawAt(textView, drawingContext, _closePos, brush);
        if (_matched) DrawGuide(textView, drawingContext);
    }

    private static void DrawAt(TextView textView, DrawingContext ctx, int offset, IBrush brush)
    {
        if (offset < 0 || offset >= textView.Document.TextLength) return;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
        builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + 1 });
        var geo = builder.CreateGeometry();
        if (geo != null) ctx.DrawGeometry(brush, null, geo);
    }

    // From below the opening brace's line to above the closing brace's line, at the closing brace's
    // column — the block's indentation, also when the opening brace ends a line (`if (x) {`). Lines
    // scrolled out of view are constructed by the text view as needed; a collapsed block (both braces
    // on one visual line) gets no guide.
    private void DrawGuide(TextView textView, DrawingContext ctx)
    {
        var doc = textView.Document;
        if (_openPos < 0 || _closePos <= _openPos || _closePos >= doc.TextLength) return;
        var openLine = doc.GetLineByOffset(_openPos);
        var closeLine = doc.GetLineByOffset(_closePos);
        if (openLine.LineNumber == closeLine.LineNumber) return;

        var close = new TextViewPosition(doc.GetLocation(_closePos));
        var top = textView.GetVisualPosition(new TextViewPosition(doc.GetLocation(_openPos)), VisualYPosition.LineBottom)
                  - textView.ScrollOffset;
        var bottom = textView.GetVisualPosition(close, VisualYPosition.LineTop) - textView.ScrollOffset;
        if (bottom.Y - top.Y < 1) return;

        var x = Math.Floor(bottom.X) + 0.5; // pixel-aligned
        var brush = GutterMetrics.ThemedResource<IBrush>("Invoklet.Editor.ActiveScopeGuideBrush") ?? GuideBrush;
        var from = new Point(x, Math.Max(0, top.Y));
        var to = new Point(x, Math.Min(textView.Bounds.Height, bottom.Y));
        if (to.Y <= from.Y) return;
        ctx.DrawLine(new Pen(brush, 1.0), from, to);
        LastGuide = (from, to);
    }
}
