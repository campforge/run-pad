using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

// CodeFixMargin used a fixed width (TextView.DefaultLineHeight * 0.8) independent of
// BreakPointMargin's own line-number-driven width, so the lightbulb column sat narrower than the
// line-number/debug-arrow column right next to it and the two never lined up. Both margins now
// measure through this one shared formula instead of each keeping its own copy, so a future change
// to the sizing rule can't make them drift apart again.
internal static class GutterMetrics
{
    public static double LineNumberColumnWidth(CodeTextEditor editor, TextView textView, double horizontalPadding)
    {
        var digits = Math.Max(2, textView.Document.LineCount.ToString(CultureInfo.InvariantCulture).Length);
        var probe = new string('8', digits);
        var typeface = new Typeface(editor.FontFamily);
        var text = new FormattedText(probe, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, editor.FontSize, Brushes.Black);
        return Math.Max(textView.DefaultLineHeight, text.Width + horizontalPadding * 2);
    }

    // ── Shared by every gutter column ────────────────────────────────────────

    /// <summary>Top and height of a document line's row in the gutter (a margin's coordinates), or
    /// null when the line is not on screen.</summary>
    public static (double Top, double Height)? Row(TextView textView, int lineNumber)
    {
        if (!textView.VisualLinesValid) return null;
        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine.LineNumber > lineNumber) break;
            if (lineNumber > vl.LastDocumentLine.LineNumber) continue;
            var top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
            return (top, vl.Height);
        }
        return null;
    }

    /// <summary>The subtle current-line band each gutter column paints behind the caret's row, so the
    /// row reads as one across the gutter.</summary>
    public static void DrawCurrentLineRow(DrawingContext context, TextView textView, int caretLine, double width)
    {
        if (Row(textView, caretLine) is not var (top, height)) return;
        if (ThemedResource<IBrush>("RunPad.Margin.CurrentLineBrush") is { } brush)
            context.FillRectangle(brush, new Rect(0, top, width, height));
    }

    public static T? ThemedResource<T>(string key) where T : class
    {
        object? res = null;
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out res);
        return res as T;
    }
}
