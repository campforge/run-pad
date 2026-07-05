using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace DotPad.Avalonia.CodeTextEditor;

public class BreakPointMargin : AbstractMargin, IDisposable
{
    static BreakPointMargin()
    {
        FocusableProperty.OverrideDefaultValue(typeof(BreakPointMargin), false);
    }

    private CodeTextEditor? _editor;
    private IBreakpointStore? _manager;

    private int _previewLine = -1;
    private bool _previewPointVisible;

    // Loaded once from app resources; geometries are immutable
    private StreamGeometry? _breakpointGeo;
    private StreamGeometry? _debugArrowGeo;

    public int? DebugLine { get; set; }

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

        context.FillRectangle(_editor?.Background ?? Brushes.Honeydew, Bounds);

        _breakpointGeo ??= LoadResource<StreamGeometry>("Icon.Breakpoint");
        _debugArrowGeo ??= LoadResource<StreamGeometry>("Icon.DebugArrow");

        var bpBrush = LoadThemedResource<IBrush>("DotPad.Margin.BreakpointBrush");
        var verifiedBrush = LoadThemedResource<IBrush>("DotPad.Margin.BreakpointVerifiedBrush");
        var unverifiedBrush = LoadThemedResource<IBrush>("DotPad.Margin.BreakpointUnverifiedBrush");
        var previewBrush = LoadThemedResource<IBrush>("DotPad.Margin.BreakpointPreviewBrush");
        var arrowBrush = LoadThemedResource<IBrush>("DotPad.Margin.DebugArrowBrush");

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
                var stateBrush = bpAtLine!.VerificationState switch
                {
                    BreakpointVerificationState.Verified => verifiedBrush ?? bpBrush,
                    BreakpointVerificationState.Unverified => unverifiedBrush ?? bpBrush,
                    _ => bpBrush,
                };
                DrawCentered(context, _breakpointGeo, stateBrush, yTop, lineH);
            }
            else if (isHover && _breakpointGeo is not null)
            {
                DrawCentered(context, _breakpointGeo, previewBrush, yTop, lineH);
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
        => TextView != null ? new Size(TextView.DefaultLineHeight, 0) : new Size(0, 0);

    public void Dispose()
    {
        _editor = null;
        _manager = null;
        GC.SuppressFinalize(this);
    }

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
