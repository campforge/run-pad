using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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

    public BreakPointMargin(CodeTextEditor editor, IBreakpointStore manager)
    {
        _editor = editor;
        _manager = manager;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        if (!TextView.VisualLinesValid || TextView.VisualLines.Count == 0)
            return;

        context.FillRectangle(_editor?.Background ?? Brushes.Honeydew, Bounds);

        var height = TextView.VisualLines[0].Height;

        foreach (var bp in _manager?.OfType<IBreakpoint>() ?? [])
        {
            var line = FindVisualLine(bp.Line);
            if (line != null)
                DrawPoint(context, line, height, Brush.Parse("#FF3737"));
        }

        if (_previewPointVisible && _previewLine >= 0)
        {
            var line = FindVisualLine(_previewLine);
            if (line != null)
                DrawPoint(context, line, height, Brush.Parse("#E67466"));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (UpdatePreviewLine(e))
            InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _previewLine = -1;
        _previewPointVisible = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_editor is null || _manager is null) return;

        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));
        if (offset == -1) return;

        _previewPointVisible = true;
        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;
        var existing = _manager.FirstOrDefault(bp => bp.Line == lineNumber);

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

    private VisualLine? FindVisualLine(int lineNumber)
        => TextView.VisualLines.FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == lineNumber);

    private void DrawPoint(DrawingContext context, VisualLine line, double height, IBrush brush)
    {
        var size = Bounds.Size;
        var yTop = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop);
        var point = new Point((size.Width / 4) - 1, yTop + (size.Width / 4) - 3.5 - TextView.VerticalOffset);
        context.FillRectangle(brush,
            new Rect(point.X, point.Y, size.Width / 1.5, height / 1.5), (float)height);
    }
}
