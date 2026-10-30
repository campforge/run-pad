using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.CodeTextEditor.Margins;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeTextEditor;

// The editor gutter, Rider-style: a thin separator between the gutter and the code, chevrons of
// expanded blocks only while the folding column is hovered (collapsed ones always), and the brace
// pair at the caret with its scope guide emphasized. Real pointer events where hovering or clicking
// matters.
public class GutterAppearanceTests
{
    private const string Code = """
        void Main()
        {
            if (ok) { Run(); }
            if (more)
            {
                Run();
            }
        }
        """;

    private static (Window Window, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor Editor) CreateEditor(string text = Code)
    {
        var editor = new Invoklet.Avalonia.CodeTextEditor.CodeTextEditor { Document = new TextDocument(text), ShowBreakpoints = true };
        // what the C# language profile supplies in the app
        editor.BracePairs['('] = ')';
        editor.BracePairs['{'] = '}';
        var window = new Window { Content = editor, Width = 600, Height = 300 };
        window.Show();
        Settle(editor);
        return (window, editor);
    }

    private static void Settle(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor editor)
    {
        editor.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        editor.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static FoldingMargin Folding(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor editor) => editor.FoldingMarginForTests!;

    private static Point InWindow(Window window, Visual visual, Point point) => visual.TranslatePoint(point, window)!.Value;

    private static double RowCenter(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor editor, int line)
        => GutterMetrics.Row(editor.TextArea.TextView, line) is var (top, height) ? top + height / 2 : throw new InvalidOperationException();

    // ── Separator ───────────────────────────────────────────────────────────

    // One thin line between the whole gutter and the code — the last left margin, whichever margins
    // are shown — and it takes no clicks.
    [AvaloniaFact]
    public void TheSeparator_Should_BeTheLastGutterColumn_OneLogicalPixel_AndTakeNoClicks()
    {
        var (_, editor) = CreateEditor();
        var separator = editor.GutterSeparatorForTests;

        Assert.Same(separator, editor.TextArea.LeftMargins[^1]);
        Assert.Contains(editor.TextArea.LeftMargins, m => m is FoldingMargin);
        Assert.Equal(GutterSeparatorMargin.Thickness, separator.Bounds.Width, 3);
        Assert.False(separator.IsHitTestVisible);

        editor.ShowBreakpoints = false;
        editor.ShowBreakpoints = true;
        editor.ShowFolding = false;
        editor.ShowFolding = true;
        Settle(editor);
        Assert.Same(separator, editor.TextArea.LeftMargins[^1]);
    }

    // ── Folding chevrons ─────────────────────────────────────────────────────

    // Expanded blocks: no chevron until the pointer is over the folding column, then all of them;
    // leaving the column hides them again. Collapsed blocks: always. A single-line pair never has one.
    [AvaloniaFact]
    public void ExpandedChevrons_Should_ShowOnlyWhileTheFoldingColumnIsHovered_CollapsedOnesAlways()
    {
        var (window, editor) = CreateEditor();
        var folding = Folding(editor);
        var width = folding.Bounds.Width;
        Assert.Empty(folding.VisibleChevrons());

        // Hover a row with no chevron of its own: the whole column reveals them.
        window.MouseMove(InWindow(window, folding, new Point(width / 2, RowCenter(editor, 6))));
        Settle(editor);
        Assert.True(folding.IsColumnHovered);
        Assert.Equal([(2, false), (5, false)], folding.VisibleChevrons());
        Assert.Equal(width, folding.Bounds.Width); // hovering never changes the layout

        // Collapse the inner block with a real click on its chevron.
        var chevron = InWindow(window, folding, new Point(width / 2, RowCenter(editor, 5)));
        window.MouseDown(chevron, MouseButton.Left);
        window.MouseUp(chevron, MouseButton.Left);
        Settle(editor);
        Assert.Contains((5, true), folding.VisibleChevrons());

        // Into the code: only the collapsed block keeps its chevron.
        window.MouseMove(InWindow(window, editor.TextArea.TextView, new Point(40, RowCenter(editor, 2))));
        Settle(editor);
        Assert.False(folding.IsColumnHovered);
        Assert.Equal([(5, true)], folding.VisibleChevrons());
        Assert.Equal(width, folding.Bounds.Width);
    }

    [AvaloniaFact]
    public void ContextClickOnBreakpoint_Should_EditIt_WithoutTogglingIt()
    {
        var (window, editor) = CreateEditor();
        try
        {
            editor.AddBreakpoint(3);
            var original = Assert.Single(editor.Breakpoints);
            var edited = 0;
            editor.BreakpointRightClicked += (line, bp) =>
            {
                Assert.Equal(3, line);
                Assert.Same(original, bp);
                edited++;
            };
            var margin = editor.TextArea.LeftMargins.OfType<BreakPointMargin>().Single();
            var point = InWindow(window, margin, new Point(margin.Bounds.Width / 2, RowCenter(editor, 3)));
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Assert.Equal(1, edited);
            Assert.Same(original, Assert.Single(editor.Breakpoints));
            if (OperatingSystem.IsMacOS())
            {
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
                Assert.Equal(2, edited);
                Assert.Same(original, Assert.Single(editor.Breakpoints));
            }
        }
        finally { window.Close(); }
    }

    // A click in the folding column beside a chevron does not move the caret into the code.
    [AvaloniaFact]
    public void AClickInTheFoldingColumn_WithoutAChevron_Should_LeaveTheCaret()
    {
        var (window, editor) = CreateEditor();
        editor.TextArea.Caret.Line = 3;
        editor.TextArea.Caret.Column = 5;
        var folding = Folding(editor);

        var point = InWindow(window, folding, new Point(folding.Bounds.Width / 2, RowCenter(editor, 6)));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(editor);

        Assert.Equal((3, 5), (editor.TextArea.Caret.Line, editor.TextArea.Caret.Column));
    }

    // ── Brace pair and active scope ──────────────────────────────────────────

    private static int OffsetOf(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor editor, int line, string text)
    {
        var docLine = editor.Document.GetLineByNumber(line);
        return docLine.Offset + editor.Document.GetText(docLine).IndexOf(text, StringComparison.Ordinal);
    }

    // At a brace of a multi-line block: both braces are matched, the block's scope is the active one
    // in the folding column, and the guide in the code runs between the brace lines at the closing
    // brace's column. Moving away clears it.
    [AvaloniaFact]
    public void AtAMultiLineBrace_TheActiveScopeGuide_Should_JoinThePair_AndClearWhenTheCaretMoves()
    {
        var (_, editor) = CreateEditor();
        var highlighter = editor.BraceMatchHighlighterForTests;
        var textView = editor.TextArea.TextView;

        editor.TextArea.Caret.Offset = OffsetOf(editor, 5, "{") + 1; // just after the inner block's {
        Settle(editor);

        Assert.Equal(5, Folding(editor).ActiveFoldStartLineForTests);
        var (top, bottom) = Assert.NotNull(highlighter.LastGuide);
        var (row5Top, row5Height) = GutterMetrics.Row(textView, 5)!.Value;
        var (row7Top, _) = GutterMetrics.Row(textView, 7)!.Value;
        Assert.Equal(row5Top + row5Height, top.Y, 1);
        Assert.Equal(row7Top, bottom.Y, 1);
        var closeX = textView.GetVisualPosition(
            new global::AvaloniaEdit.TextViewPosition(editor.Document.GetLocation(OffsetOf(editor, 7, "}"))),
            global::AvaloniaEdit.Rendering.VisualYPosition.LineTop).X;
        Assert.Equal(Math.Floor(closeX) + 0.5, top.X, 3);

        editor.TextArea.Caret.Offset = OffsetOf(editor, 6, "Run");
        Settle(editor);
        Assert.Null(Folding(editor).ActiveFoldStartLineForTests);
        Assert.Null(highlighter.LastGuide);
    }

    // A pair on one line is highlighted but has no guide, and no active fold.
    [AvaloniaFact]
    public void AtASingleLinePair_Should_HaveNoGuide()
    {
        var (_, editor) = CreateEditor();

        editor.TextArea.Caret.Offset = OffsetOf(editor, 3, "{") + 1;
        Settle(editor);

        Assert.Null(editor.BraceMatchHighlighterForTests.LastGuide);
        Assert.Null(Folding(editor).ActiveFoldStartLineForTests);
    }

    // A collapsed block shows both braces on one visual line: no guide, and nothing stale after
    // expanding it again.
    [AvaloniaFact]
    public void AtACollapsedBlock_Should_HaveNoGuide()
    {
        var (_, editor) = CreateEditor();
        editor.TextArea.Caret.Offset = OffsetOf(editor, 2, "{") + 1; // outer block's {
        Settle(editor);
        Assert.NotNull(editor.BraceMatchHighlighterForTests.LastGuide);

        var foldingManager = Folding(editor).ManagerForTests;
        var inner = foldingManager.AllFoldings.Single(f => editor.Document.GetLineByOffset(f.StartOffset - 1).LineNumber == 5);
        inner.IsFolded = true;
        editor.TextArea.Caret.Offset = OffsetOf(editor, 5, "{") + 1;
        Settle(editor);
        Assert.Null(editor.BraceMatchHighlighterForTests.LastGuide);

        inner.IsFolded = false;
        editor.TextArea.Caret.Offset = OffsetOf(editor, 4, "if");
        Settle(editor);
        Assert.Null(editor.BraceMatchHighlighterForTests.LastGuide);
    }

    // ── Current line ─────────────────────────────────────────────────────────

    // Every gutter column paints the caret's row from the same helper: the row follows the caret,
    // including onto a wrapped line's full height.
    [AvaloniaFact]
    public void TheCurrentLineRow_Should_FollowTheCaret()
    {
        var (_, editor) = CreateEditor();
        var textView = editor.TextArea.TextView;

        editor.TextArea.Caret.Line = 6;
        Settle(editor);
        var row = GutterMetrics.Row(textView, editor.TextArea.Caret.Line);
        Assert.Equal(RowCenter(editor, 6), row!.Value.Top + row.Value.Height / 2, 3);

        Assert.Null(GutterMetrics.Row(textView, 500)); // not on screen
    }
}
