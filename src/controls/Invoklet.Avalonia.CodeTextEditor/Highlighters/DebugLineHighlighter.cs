using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace Invoklet.Avalonia.CodeTextEditor;

internal sealed class DebugLineHighlighter : IBackgroundRenderer
{
    private static readonly IBrush FallbackBrush = new SolidColorBrush(Color.FromArgb(55, 255, 216, 0));

    public int? CurrentLine { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (CurrentLine is not { } line) return;
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        var visualLine = textView.VisualLines
            .FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == line);
        if (visualLine is null) return;

        object? res = null;
        Application.Current?.TryGetResource("Invoklet.Margin.DebugLineBrush",
            Application.Current?.ActualThemeVariant, out res);
        var brush = res as IBrush ?? FallbackBrush;

        var yTop = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.LineTop)
                   - textView.VerticalOffset;

        drawingContext.FillRectangle(brush, new Rect(0, yTop, textView.Bounds.Width, visualLine.Height));
    }
}
