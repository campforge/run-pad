using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #75: the lightbulb margin used to paint every fix with the same neutral color and measure
// its own column independently of BreakPointMargin's line-number/debug-arrow column right next to
// it. These exercise the two fixes directly: severity picks the right themed brush key, and the
// margin's measured width now comes from the same GutterMetrics formula BreakPointMargin uses.
public class CodeFixMarginTests
{
    private static (Window Window, CodeTextEditor Editor, CodeFixMargin Margin) CreateEditor(string text)
    {
        var editor = new CodeTextEditor { Document = new TextDocument(text) };
        var window = new Window { Content = editor };
        window.Show();
        var margin = editor.TextArea.LeftMargins.OfType<CodeFixMargin>().Single();
        return (window, editor, margin);
    }

    [AvaloniaFact]
    public void WorstSeverityOnLine_Should_ReturnError_WhenTheLineHasBothAnErrorAndAWarningFix()
    {
        var (_, _, margin) = CreateEditor("x\n");

        margin.Update(
        [
            new CodeFixEntry(1, "warning fix", "x\n", [], CodeFixSeverity.Warning),
            new CodeFixEntry(1, "error fix", "x\n", [], CodeFixSeverity.Error),
        ]);

        Assert.Equal(CodeFixSeverity.Error, margin.WorstSeverityOnLine(1));
    }

    [AvaloniaFact]
    public void WorstSeverityOnLine_Should_ReturnNull_ForALineWithNoFixes()
    {
        var (_, _, margin) = CreateEditor("x\n");

        margin.Update([new CodeFixEntry(1, "fix", "x\n", [], CodeFixSeverity.Warning)]);

        Assert.Null(margin.WorstSeverityOnLine(2));
    }

    [Theory]
    [InlineData(CodeFixSeverity.Error, "DotPad.Margin.CodeFixErrorBrush")]
    [InlineData(CodeFixSeverity.Warning, "DotPad.Margin.CodeFixWarningBrush")]
    [InlineData(CodeFixSeverity.Info, "DotPad.Margin.DebugArrowBrush")]
    public void SeverityBrushKey_Should_MapEachSeverityToItsOwnThemeResourceKey(
        CodeFixSeverity severity, string expectedKey)
    {
        Assert.Equal(expectedKey, CodeFixMargin.SeverityBrushKey(severity));
    }

    // Regression guard for the alignment half of #75: before the fix, MeasureOverride returned
    // TextView.DefaultLineHeight * 0.8 regardless of the shared gutter formula — a value that
    // diverges from GutterMetrics as soon as the document needs more than the 2-digit minimum (e.g.
    // 100+ lines), and is narrower even at the minimum. Comparing directly against GutterMetrics
    // (the same helper BreakPointMargin's own width now goes through) is what actually proves the
    // two margins stay in lockstep, rather than merely asserting some specific pixel value.
    [AvaloniaFact]
    public void MeasureOverride_Should_MatchGutterMetrics_LineNumberColumnWidth()
    {
        var (_, editor, margin) = CreateEditor(string.Concat(Enumerable.Repeat("line\n", 150)));

        margin.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var expected = GutterMetrics.LineNumberColumnWidth(editor, editor.TextArea.TextView, 6.0);

        Assert.Equal(expected, margin.DesiredSize.Width, precision: 3);
    }
}
