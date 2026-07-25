using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

internal sealed class FoldingMargin : AbstractMargin
{
    private readonly FoldingManager _manager;
    private FoldingSection? _hovered;

    private const double W = 16.0; // gutter width — wide enough to hit easily

    // Open-stroke chevrons in a 10×10 space; scaled at render time
    private static readonly StreamGeometry _expandedGeo = BuildChevron(down: true);
    private static readonly StreamGeometry _collapsedGeo = BuildChevron(down: false);

    public FoldingMargin(FoldingManager manager)
    {
        _manager = manager;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    protected override Size MeasureOverride(Size _) => new(W, 0);

    public override void Render(DrawingContext ctx)
    {
        var tv = TextView;
        var doc = Document;
        if (tv is null || doc is null || !tv.VisualLinesValid || tv.VisualLines.Count == 0) return;

        var chevronBrush = LoadThemedResource<IBrush>("RunPad.Folding.ChevronBrush");
        var hoverBrush = LoadThemedResource<IBrush>("RunPad.Folding.ChevronHoverBrush");
        var lineBrush = LoadThemedResource<IBrush>("RunPad.Folding.LineBrush");

        // ── Pass 1: thin vertical scope line for each expanded fold ──────────
        if (lineBrush is not null)
        {
            var linePen = new Pen(lineBrush, 1.0);
            var cx = Math.Floor(W / 2.0) + 0.5; // pixel-aligned centre

            foreach (var section in _manager.AllFoldings)
            {
                if (section.IsFolded) continue;

                var bracePos = Math.Max(0, section.StartOffset - 1);
                double? yStart = null;
                double yEnd = 0;
                bool hasEnd = false;

                foreach (var vl in tv.VisualLines)
                {
                    var lo = vl.FirstDocumentLine.Offset;
                    var lt = lo + vl.FirstDocumentLine.TotalLength;
                    var yT = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - tv.VerticalOffset;
                    var yB = yT + vl.Height;

                    if (bracePos >= lo && bracePos < lt)
                    {
                        yStart = yB; // scope line starts below the { line
                        continue;
                    }
                    if (yStart is null) continue;

                    if (section.EndOffset >= lo && section.EndOffset < lt)
                    {
                        yEnd = yT; hasEnd = true; break; // stop at top of } line
                    }
                    if (lo >= section.StartOffset && lo < section.EndOffset)
                    {
                        yEnd = yB; hasEnd = true; // extend through inner lines
                    }
                }

                if (yStart.HasValue && hasEnd && yEnd > yStart.Value)
                    ctx.DrawLine(linePen, new Point(cx, yStart.Value), new Point(cx, yEnd));
            }
        }

        // ── Pass 2: chevron at the start line of every fold ───────────────────
        foreach (var vl in tv.VisualLines)
        {
            var section = FindFoldStartOnLine(vl, doc);
            if (section is null) continue;

            var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - tv.VerticalOffset;
            var yMid = yTop + vl.Height / 2.0;
            var isHover = section == _hovered;

            // Subtle background highlight across the full line height on hover
            if (isHover)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                                  new Rect(0, yTop, W, vl.Height));

            IBrush brush = (isHover ? hoverBrush : chevronBrush)
                           ?? new SolidColorBrush(isHover ? Colors.White : Color.FromRgb(100, 100, 100));
            var geo = section.IsFolded ? _collapsedGeo : _expandedGeo;

            // Scale 10×10 chevron to (W-4)×(W-4) and centre it vertically
            var s = (W - 4.0) / 10.0;
            var ox = (W - 10.0 * s) / 2.0;
            var oy = yMid - 5.0 * s;

            using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            {
                ctx.DrawGeometry(null,
                    new Pen(brush, thickness: 1.5 / s, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                    geo);
            }
        }
    }

    // ── Pointer handling ──────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var section = HitTest(e.GetPosition(this));
        if (section is null) return;
        section.IsFolded = !section.IsFolded;
        TextView?.InvalidateMeasure();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var section = HitTest(e.GetPosition(this));
        if (section == _hovered) return;
        _hovered = section;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is null) return;
        _hovered = null;
        InvalidateVisual();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns the folding section whose opening brace { lives on the given visual line.
    // BraceFoldingStrategy stores StartOffset = braceIndex + 1, so { is at StartOffset - 1.
    private FoldingSection? FindFoldStartOnLine(VisualLine vl, TextDocument doc)
    {
        var lineNum = vl.FirstDocumentLine.LineNumber;
        foreach (var fs in _manager.AllFoldings)
        {
            var braceOffset = Math.Max(0, fs.StartOffset - 1);
            if (doc.GetLineByOffset(braceOffset).LineNumber == lineNum)
                return fs;
        }
        return null;
    }

    private FoldingSection? HitTest(Point pt)
    {
        var tv = TextView;
        var doc = Document;
        if (tv is null || doc is null || !tv.VisualLinesValid) return null;

        foreach (var vl in tv.VisualLines)
        {
            var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - tv.VerticalOffset;
            if (pt.Y < yTop || pt.Y >= yTop + vl.Height) continue;
            return FindFoldStartOnLine(vl, doc);
        }
        return null;
    }

    // ── Geometry builders ─────────────────────────────────────────────────────

    // down=true  → ∨ (expanded)   M2,3.5 L5,7 L8,3.5
    // down=false → › (collapsed)  M3.5,2 L7,5 L3.5,8
    private static StreamGeometry BuildChevron(bool down)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        if (down)
        {
            ctx.BeginFigure(new Point(2, 3.5), false);
            ctx.LineTo(new Point(5, 7));
            ctx.LineTo(new Point(8, 3.5));
        }
        else
        {
            ctx.BeginFigure(new Point(3.5, 2), false);
            ctx.LineTo(new Point(7, 5));
            ctx.LineTo(new Point(3.5, 8));
        }
        ctx.EndFigure(false);
        return g;
    }

    // ── Resource helpers ──────────────────────────────────────────────────────

    private static T? LoadThemedResource<T>(string key) where T : class
    {
        object? res = null;
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out res);
        return res as T;
    }
}
