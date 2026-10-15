using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

// Folding chevrons, Rider-style: a collapsed block's chevron is always shown; an expanded block's
// only while the pointer is over this column — anywhere in it, revealing all of them at once — so
// the gutter stays quiet while reading code. Hovering changes only colors and visibility, never the
// layout. Also the thin scope line of each expanded block, the active one (the block whose braces
// the caret is at) emphasized.
internal sealed class FoldingMargin : AbstractMargin
{
    private readonly FoldingManager _manager;
    private FoldingSection? _hovered;
    private bool _columnHovered;
    private (int Open, int Close)? _activeBraces;

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

    /// <summary>The matched brace pair at the caret, if any: the block it encloses gets the active scope
    /// line.</summary>
    public void SetActiveBraces(int open, int close)
    {
        (int, int)? value = open >= 0 && close >= 0 ? (open, close) : null;
        if (_activeBraces == value) return;
        _activeBraces = value;
        InvalidateVisual();
    }

    // Test-only (InternalsVisibleTo).
    internal bool IsColumnHovered => _columnHovered;
    internal FoldingManager ManagerForTests => _manager;
    internal int? ActiveFoldStartLineForTests => ActiveSection() is { } section && Document is { } doc
        ? doc.GetLineByOffset(section.StartOffset - 1).LineNumber
        : null;

    // Which chevrons are drawn now: (line, collapsed) for each fold starting on a visible line —
    // collapsed ones always, expanded ones only while the column is hovered.
    internal IReadOnlyList<(int Line, bool Collapsed)> VisibleChevrons()
    {
        var tv = TextView;
        var doc = Document;
        if (tv is null || doc is null || !tv.VisualLinesValid) return [];
        var result = new List<(int, bool)>();
        foreach (var vl in tv.VisualLines)
        {
            if (FindFoldStartOnLine(vl, doc) is not { } section) continue;
            if (section.IsFolded || _columnHovered)
                result.Add((vl.FirstDocumentLine.LineNumber, section.IsFolded));
        }
        return result;
    }

    private FoldingSection? ActiveSection()
    {
        if (_activeBraces is not var (open, close)) return null;
        // BraceFoldingStrategy: StartOffset = the opening brace + 1, EndOffset = the closing brace.
        return _manager.AllFoldings.FirstOrDefault(f => f.StartOffset - 1 == open && f.EndOffset == close);
    }

    public override void Render(DrawingContext ctx)
    {
        var tv = TextView;
        var doc = Document;
        if (tv is null || doc is null || !tv.VisualLinesValid || tv.VisualLines.Count == 0) return;

        // Transparent, but it makes the whole column hit-testable: with the expanded chevrons hidden
        // nothing else is drawn there, and the pointer must still "hover the column" to reveal them.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextArea is { } textArea)
            GutterMetrics.DrawCurrentLineRow(ctx, tv, textArea.Caret.Line, Bounds.Width);

        var chevronBrush = LoadThemedResource<IBrush>("RunPad.Folding.ChevronBrush");
        var hoverBrush = LoadThemedResource<IBrush>("RunPad.Folding.ChevronHoverBrush");
        var lineBrush = LoadThemedResource<IBrush>("RunPad.Folding.LineBrush");
        var activeBrush = LoadThemedResource<IBrush>("RunPad.Editor.ActiveScopeGuideBrush") ?? lineBrush;
        var active = ActiveSection();

        // ── Pass 1: thin vertical scope line for each expanded fold ──────────
        if (lineBrush is not null)
        {
            var linePen = new Pen(lineBrush, 1.0);
            var activePen = new Pen(activeBrush, 1.0);
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
                    ctx.DrawLine(section == active ? activePen : linePen, new Point(cx, yStart.Value), new Point(cx, yEnd));
            }
        }

        // ── Pass 2: chevron at the start line of every fold ───────────────────
        foreach (var vl in tv.VisualLines)
        {
            var section = FindFoldStartOnLine(vl, doc);
            if (section is null) continue;
            if (!section.IsFolded && !_columnHovered) continue; // expanded: only while the column is hovered

            var (rowTop, rowHeight) = GutterMetrics.FirstRow(tv, vl);
            var yMid = rowTop + rowHeight / 2.0;
            var isHover = section == _hovered;

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
        // Presses in the column stay here: a click beside a chevron must not move the caret.
        e.Handled = true;
        var section = HitTest(e.GetPosition(this));
        if (section is null) return;
        section.IsFolded = !section.IsFolded;
        TextView?.InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _columnHovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var section = HitTest(e.GetPosition(this));
        if (_columnHovered && section == _hovered) return;
        _columnHovered = true;
        _hovered = section;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _columnHovered = false;
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
