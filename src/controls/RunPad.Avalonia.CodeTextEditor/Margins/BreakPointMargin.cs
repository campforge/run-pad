using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

// Combined line-number + breakpoint gutter: a single wide column with one click handler for both,
// instead of AvaloniaEdit's built-in LineNumberMargin sitting as a separate narrow column next to
// ours. Two adjacent narrow columns made it easy to miss the breakpoint column when clicking
// (see BUG-B in ANALYSIS.md) — CodeTextEditor now keeps ShowLineNumbers off and relies on this
// margin to render the number itself, hidden behind the breakpoint/debug-arrow icon on lines that
// have one.
public class BreakPointMargin : AbstractMargin, IDisposable
{
    private const double HorizontalPadding = 6.0;

    static BreakPointMargin()
    {
        FocusableProperty.OverrideDefaultValue(typeof(BreakPointMargin), false);
    }

    private CodeTextEditor? _editor;
    private IBreakpointStore? _manager;

    private int _previewLine = -1;
    private bool _previewPointVisible;
    private bool _measureInvalidationPending;

    // Loaded once from app resources; geometries are immutable
    private StreamGeometry? _breakpointGeo;
    private StreamGeometry? _debugArrowGeo;
    private StreamGeometry? _conditionBadgeGeo;

    public int? DebugLine { get; set; }

    // Backs CodeTextEditor.ShowLineNumberText — separate from the base TextEditor.ShowLineNumbers
    // property, which this margin permanently keeps off (see CodeTextEditor ctor) to avoid
    // AvaloniaEdit reinserting its own separate LineNumberMargin column.
    public bool ShowLineNumbers { get; set; } = true;

    public BreakPointMargin(CodeTextEditor editor, IBreakpointStore manager)
    {
        _editor = editor;
        _manager = manager;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        if (!TextView.VisualLinesValid || TextView.VisualLines.Count == 0) return;

        // The column widens as the document gains digits (e.g. 9 → 10, 99 → 100 lines). Cheaper to
        // just compare against the desired width on every render than to wire up a document
        // line-count-changed subscription for what's a rare, one-pixel-relevant event.
        // InvalidateMeasure() must NOT be called synchronously here — Avalonia throws
        // "Visual was invalidated during the render pass" if a render callback invalidates layout
        // for itself mid-pass. Deferring via Dispatcher.Post re-measures right after this pass ends.
        var desiredWidth = GetDesiredWidth();
        if (Math.Abs(desiredWidth - Bounds.Width) > 0.5 && !_measureInvalidationPending)
        {
            _measureInvalidationPending = true;
            Dispatcher.UIThread.Post(() => { _measureInvalidationPending = false; InvalidateMeasure(); });
        }

        context.FillRectangle(_editor?.Background ?? Brushes.Honeydew, Bounds);

        _breakpointGeo ??= LoadResource<StreamGeometry>("Icon.Breakpoint");
        _debugArrowGeo ??= LoadResource<StreamGeometry>("Icon.DebugArrow");
        _conditionBadgeGeo ??= LoadResource<StreamGeometry>("Icon.BreakpointCondition");

        var bpBrush = LoadThemedResource<IBrush>("RunPad.Margin.BreakpointBrush");
        var verifiedBrush = LoadThemedResource<IBrush>("RunPad.Margin.BreakpointVerifiedBrush");
        var unverifiedBrush = LoadThemedResource<IBrush>("RunPad.Margin.BreakpointUnverifiedBrush");
        var previewBrush = LoadThemedResource<IBrush>("RunPad.Margin.BreakpointPreviewBrush");
        var conditionBadgeBrush = LoadThemedResource<IBrush>("RunPad.Margin.BreakpointConditionBadgeBrush");
        var arrowBrush = LoadThemedResource<IBrush>("RunPad.Margin.DebugArrowBrush");
        var lineNumberBrush = LoadThemedResource<IBrush>("RunPad.Margin.LineNumberBrush");

        foreach (var vl in TextView.VisualLines)
        {
            var lineNo = vl.FirstDocumentLine.LineNumber;
            var lineH = vl.Height;
            var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop)
                          - TextView.VerticalOffset;
            var bpAtLine = _manager?.FirstOrDefault(bp => bp.Line == lineNo);
            var hasBp = bpAtLine is not null;
            var isDebug = DebugLine == lineNo;
            var isHover = _previewPointVisible && _previewLine == lineNo;

            if (isDebug && _debugArrowGeo is not null)
            {
                // When stopped at a breakpoint, arrow turns red to show the overlap state
                DrawFullWidth(context, _debugArrowGeo, hasBp ? bpBrush : arrowBrush, yTop, lineH);
            }
            else if (hasBp && _breakpointGeo is not null)
            {
                var plan = ComputeBreakpointIconPlan(bpAtLine!);

                // TODO.md #83: unverified used to be the same filled disc, just recolored amber —
                // now a hollow ring (VS Code's own convention for a not-yet-bound breakpoint), an
                // actual shape difference rather than color alone.
                if (plan.DrawOutline)
                    DrawCenteredOutline(context, _breakpointGeo, unverifiedBrush ?? bpBrush, yTop, lineH);
                else
                {
                    var stateBrush = bpAtLine!.VerificationState == BreakpointVerificationState.Verified
                        ? verifiedBrush ?? bpBrush
                        : bpBrush;
                    DrawCentered(context, _breakpointGeo, stateBrush, yTop, lineH);
                }

                // TODO.md #83: conditional/hit-count breakpoints (IBreakpoint.Condition/HitCondition,
                // TODO #45) used to be visually identical to a plain breakpoint — small diamond
                // overlay, (Code)Rider-style, drawn with the same centering transform so it lands in
                // the base icon's bottom-right corner regardless of verification state above.
                if (plan.DrawConditionBadge && _conditionBadgeGeo is not null)
                    DrawCentered(context, _conditionBadgeGeo, conditionBadgeBrush, yTop, lineH);
            }
            else if (isHover && _breakpointGeo is not null)
            {
                DrawCentered(context, _breakpointGeo, previewBrush, yTop, lineH);
            }
            else if (ShowLineNumbers)
            {
                DrawLineNumber(context, lineNo, lineNumberBrush, yTop, lineH);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (UpdatePreviewLine(e))
            InvalidateVisual();

        UpdateVerificationTooltip(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _previewLine = -1;
        _previewPointVisible = false;
        InvalidateVisual();
        ToolTip.SetTip(this, null);
    }

    // Shows the debugger's rejection reason (e.g. "No code at line 5") while hovering an
    // unverified breakpoint. Verified/pending breakpoints have nothing to add beyond the color.
    private void UpdateVerificationTooltip(PointerEventArgs e)
    {
        var offset = _editor?.GetOffsetFromPoint(e.GetPosition(this)) ?? -1;
        if (offset == -1) { ToolTip.SetTip(this, null); return; }

        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;
        var bp = _manager?.FirstOrDefault(b => b.Line == lineNumber);
        ToolTip.SetTip(this,
            bp?.VerificationState == BreakpointVerificationState.Unverified ? bp.VerificationMessage : null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_editor is null || _manager is null) return;

        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));
        if (offset == -1) return;

        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;
        var existing = _manager.FirstOrDefault(bp => bp.Line == lineNumber);

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _editor.RaiseBreakpointRightClicked(lineNumber, existing);
            e.Handled = true;
            return;
        }

        _previewPointVisible = true;
        if (existing != null)
        {
            _previewLine = lineNumber;
            _manager.Remove(existing);
        }
        else
        {
            _previewLine = -1;
            _manager.Add(lineNumber);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(GetDesiredWidth(), 0);

    private double GetDesiredWidth()
    {
        if (TextView is null) return 0;
        if (!ShowLineNumbers) return TextView.DefaultLineHeight;
        if (_editor is null || TextView.Document is null)
            return Math.Max(TextView.DefaultLineHeight, HorizontalPadding * 2);
        return GutterMetrics.LineNumberColumnWidth(_editor, TextView, HorizontalPadding);
    }

    private void DrawLineNumber(DrawingContext ctx, int lineNo, IBrush? brush, double yTop, double lineH)
    {
        if (_editor is null || brush is null) return;
        var typeface = new Typeface(_editor.FontFamily);
        var text = new FormattedText(lineNo.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, _editor.FontSize, brush);
        var x = Bounds.Width - text.Width - HorizontalPadding;
        var y = yTop + (lineH - text.Height) / 2;
        ctx.DrawText(text, new Point(x, y));
    }

    public void Dispose()
    {
        _editor = null;
        _manager = null;
        GC.SuppressFinalize(this);
    }

    // TODO.md #83: which icon variant a breakpoint's state maps to — pulled out of Render's drawing
    // code (which needs a live DrawingContext/TextView and so isn't unit-testable on its own) into a
    // plain function of IBreakpoint, same "extract the decision from the drawing" precedent as
    // CodeFixMargin.ComputeIconTransform (TODO #75/#82).
    internal readonly record struct BreakpointIconPlan(bool DrawOutline, bool DrawConditionBadge);

    internal static BreakpointIconPlan ComputeBreakpointIconPlan(IBreakpoint bp) => new(
        // TODO.md #83.3: disabled breakpoints get the same contour-only treatment as unverified
        // ones — a hollow ring reads as "not currently active" regardless of which of the two
        // reasons applies, without needing a second brush/geometry just to tell them apart.
        DrawOutline: !bp.IsEnabled || bp.VerificationState == BreakpointVerificationState.Unverified,
        DrawConditionBadge: bp.Condition is not null || bp.HitCondition is not null);

    // ── Drawing helpers ───────────────────────────────────────────────────────

    // Icons are defined in a 24×24 viewbox.

    private void DrawCentered(DrawingContext ctx, StreamGeometry geo, IBrush? brush,
                               double yTop, double lineH)
    {
        if (brush is null) return;
        var s = Bounds.Width * 0.72 / 24.0;
        var cx = Bounds.Width / 2 - 12 * s;
        var cy = yTop + lineH / 2 - 12 * s;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy)))
            ctx.DrawGeometry(brush, null, geo);
    }

    // TODO.md #83: unverified breakpoints — stroke-only instead of DrawCentered's fill, same
    // centering transform so the ring lands exactly where the filled disc otherwise would.
    private void DrawCenteredOutline(DrawingContext ctx, StreamGeometry geo, IBrush? brush,
                                      double yTop, double lineH)
    {
        if (brush is null) return;
        var s = Bounds.Width * 0.72 / 24.0;
        var cx = Bounds.Width / 2 - 12 * s;
        var cy = yTop + lineH / 2 - 12 * s;
        var pen = new Pen(brush, 2.2);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy)))
            ctx.DrawGeometry(null, pen, geo);
    }

    private void DrawFullWidth(DrawingContext ctx, StreamGeometry geo, IBrush? brush,
                                double yTop, double lineH)
    {
        if (brush is null) return;
        var s = Bounds.Width / 24.0;
        var cy = yTop + lineH / 2 - 12 * s;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(0, cy)))
            ctx.DrawGeometry(brush, null, geo);
    }

    // ── Resource helpers ──────────────────────────────────────────────────────

    private static T? LoadResource<T>(string key) where T : class
    {
        object? res = null;
        Application.Current?.TryGetResource(key, null, out res);
        return res as T;
    }

    private static T? LoadThemedResource<T>(string key) where T : class
    {
        object? res = null;
        var variant = Application.Current?.ActualThemeVariant;
        Application.Current?.TryGetResource(key, variant, out res);
        return res as T;
    }

    // ── Pointer helpers ───────────────────────────────────────────────────────

    private bool UpdatePreviewLine(PointerEventArgs e)
    {
        if (_editor is null) return false;

        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));
        if (offset == -1) return false;

        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;
        if (_previewPointVisible && lineNumber == _previewLine) return false;

        var hasBreakpoint = _manager?.Any(i => i.Line == lineNumber) ?? false;
        _previewPointVisible = !hasBreakpoint;
        if (_previewPointVisible)
            _previewLine = lineNumber;

        return true;
    }
}
