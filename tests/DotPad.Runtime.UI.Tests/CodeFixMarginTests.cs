using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
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
            new CodeFixEntry(1, "warning fix", CodeFixSeverity.Warning, "warn-key"),
            new CodeFixEntry(1, "error fix", CodeFixSeverity.Error, "error-key"),
        ]);

        Assert.Equal(CodeFixSeverity.Error, margin.WorstSeverityOnLine(1));
    }

    [AvaloniaFact]
    public void WorstSeverityOnLine_Should_ReturnNull_ForALineWithNoFixes()
    {
        var (_, _, margin) = CreateEditor("x\n");

        margin.Update([new CodeFixEntry(1, "fix", CodeFixSeverity.Warning, "fix-key")]);

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

    // TODO.md #82: DrawCentered's scale used to be Bounds.Width * 0.72 / 24.0 — once TODO #75 made
    // Bounds.Width track the (line-number-digit-driven) gutter width, a document needing more digits
    // inflated the icon itself right along with the column. A narrow (few lines) and a wide (150
    // lines) document give two CodeFixMargin instances with deliberately different Bounds.Width —
    // the icon's Scale must come out identical across both (tied to line height, not column width),
    // while its centering X must still differ (it has to center within whichever width the column
    // actually ended up).
    [AvaloniaFact]
    public void ComputeIconTransform_Should_KeepScaleIndependent_OfColumnWidth()
    {
        var (_, _, narrowMargin) = CreateEditor("line\n");
        var (_, _, wideMargin) = CreateEditor(string.Concat(Enumerable.Repeat("line\n", 150)));

        Arrange(narrowMargin);
        Arrange(wideMargin);

        // Sanity check: the two columns really do end up different widths, otherwise the Scale
        // equality below would hold trivially and prove nothing about the fix.
        Assert.NotEqual(narrowMargin.Bounds.Width, wideMargin.Bounds.Width);

        var narrowTransform = narrowMargin.ComputeIconTransform(lineH: 20, yTop: 0);
        var wideTransform = wideMargin.ComputeIconTransform(lineH: 20, yTop: 0);

        Assert.Equal(narrowTransform.Scale, wideTransform.Scale, precision: 6);
        Assert.NotEqual(narrowTransform.X, wideTransform.X);
    }

    // TODO.md #84 ("hard to hit the icon with the pointer"): confirms the click target was never
    // actually limited to the glyph's own pixels — a click anywhere across the margin's full row
    // width, at the same Y, already resolves to the same line. Proves the complaint was a visual
    // affordance problem (the glyph looked too small to aim at), not a hit-testing bug, which is why
    // the real fix (below) is a size bump, not a bigger click area.
    [AvaloniaFact]
    public void ResolveLineFromPoint_Should_ResolveTheSameLine_RegardlessOfXWithinTheMargin()
    {
        var (_, _, margin) = CreateEditor("line1\nline2\nline3\n");
        Arrange(margin);

        var textView = margin.TextView;
        var vl = textView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == 2);
        var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineTop)
            - textView.VerticalOffset;
        var yMid = yTop + vl.Height / 2;

        var atLeftEdge = margin.ResolveLineFromPoint(new Point(0.5, yMid));
        var atCenter = margin.ResolveLineFromPoint(new Point(margin.Bounds.Width / 2, yMid));
        var atRightEdge = margin.ResolveLineFromPoint(new Point(margin.Bounds.Width - 0.5, yMid));

        Assert.Equal(2, atLeftEdge);
        Assert.Equal(2, atCenter);
        Assert.Equal(2, atRightEdge);
    }

    // TODO.md #84: locks in the size correction itself — #82 had shrunk the glyph to 0.6x line
    // height (close to its original pre-#75 size), which read as too small to comfortably aim at
    // even though the click area was already generous (see the test above). This just bounds the
    // chosen coefficient to a sane "clearly visible, not the old column-width-scaled balloon"
    // range, so a future tweak can't silently regress back to either extreme.
    [AvaloniaFact]
    public void ComputeIconTransform_Should_ProduceAnIconCloseToLineHeight_NotTinyOrOversized()
    {
        var (_, editor, margin) = CreateEditor("line\n");
        Arrange(margin);

        var transform = margin.ComputeIconTransform(lineH: 20, yTop: 0);
        var iconWidth = transform.Scale * 24.0;

        Assert.InRange(iconWidth, editor.TextArea.TextView.DefaultLineHeight * 0.7, editor.TextArea.TextView.DefaultLineHeight * 1.0);
    }

    // TODO.md #79: the quick-fix MenuFlyout used to build each MenuItem with only a Header, no icon
    // at all. TestAppBuilder deliberately doesn't merge DotPad's own Icons.axaml (real UI tests
    // construct their own view models instead of depending on the full app composition root), so this
    // merges it in directly — the same avares:// URI App.axaml itself uses — just for this one
    // assertion, and removes it again afterward so it can't leak into other tests sharing this
    // process's one Application instance.
    [AvaloniaFact]
    public void BuildMenuItem_Should_SetTheHammerIcon()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var include = new ResourceInclude(new Uri("avares://DotPad.Runtime.UI.Tests/"))
        {
            Source = new Uri("avares://DotPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(include);
        try
        {
            var item = margin.BuildMenuItem(new CodeFixEntry(1, "Add using System;", CodeFixSeverity.Info, "fix-key"));

            var icon = Assert.IsType<PathIcon>(item.Icon);
            Assert.NotNull(icon.Data);
            Assert.Equal("Add using System;", item.Header);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(include);
        }
    }

    // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход): Apply used to set
    // _editor.Document.Text directly from CodeFixEntry.NewCode — now it just raises FixApplyRequested
    // with the clicked entry, leaving materializing/applying entirely to whoever owns
    // IIntelliSenseService (EditorTabItemControl). Locks in that the menu item built for a candidate
    // really does forward THAT candidate (not some other one) when clicked.
    [AvaloniaFact]
    public void BuildMenuItem_Click_Should_RaiseFixApplyRequested_WithTheClickedEntry()
    {
        var (_, editor, margin) = CreateEditor("x\n");
        var entry = new CodeFixEntry(1, "Add using System;", CodeFixSeverity.Info, "fix-key");

        CodeFixEntry? raised = null;
        editor.FixApplyRequested += f => raised = f;

        var item = margin.BuildMenuItem(entry);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(entry, raised);
    }

    private static void Arrange(CodeFixMargin margin)
    {
        margin.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        margin.Arrange(new Rect(margin.DesiredSize));
    }
}
