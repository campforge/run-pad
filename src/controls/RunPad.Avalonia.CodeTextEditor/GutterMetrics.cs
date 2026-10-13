using System.Globalization;
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
}
