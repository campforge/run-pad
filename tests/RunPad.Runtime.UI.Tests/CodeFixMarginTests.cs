using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit.Document;
using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The lightbulb margin used to paint every fix with the same neutral color and measure
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

    // User request 2026-07-21: the lightbulb should only ever be clickable/visible on
    // the caret's OWN line, VS/Rider-style, not on every line in the file that happens to have a fix
    // — ResolveCandidatesForClick is OnPointerPressed's own decision logic, factored out so it can be
    // driven directly with a Point (see its own doc comment for why raising a real
    // PointerPressedEventArgs isn't a practical test surface here).
    [AvaloniaFact]
    public void ResolveCandidatesForClick_Should_ReturnTheLinesFixes_WhenTheCaretIsOnThatLine()
    {
        var (_, editor, margin) = CreateEditor("line1\nline2\nline3\n");
        Arrange(margin);
        editor.TextArea.Caret.Line = 2;

        margin.Update([new CodeFixEntry(2, "fix", CodeFixSeverity.Warning, "fix-key")]);

        var candidates = margin.ResolveCandidatesForClick(PointOnLine(margin, 2));
        Assert.Single(candidates);
    }

    [AvaloniaFact]
    public void ResolveCandidatesForClick_Should_ReturnEmpty_ForALineWithAFix_WhenTheCaretIsOnADifferentLine()
    {
        var (_, editor, margin) = CreateEditor("line1\nline2\nline3\n");
        Arrange(margin);
        editor.TextArea.Caret.Line = 1; // NOT the line the fix is on

        margin.Update([new CodeFixEntry(2, "fix", CodeFixSeverity.Warning, "fix-key")]);

        var candidates = margin.ResolveCandidatesForClick(PointOnLine(margin, 2));
        Assert.Empty(candidates);
    }

    // Confirms the caret-line tracking driving the above actually updates as the caret moves, not
    // just once at construction — the same fix at line 2 is unreachable while the caret sits on line
    // 1, then becomes reachable the instant the caret moves to line 2, with no new margin.Update call
    // in between.
    [AvaloniaFact]
    public void ResolveCandidatesForClick_Should_TrackTheCaretMovingToANewLine()
    {
        var (_, editor, margin) = CreateEditor("line1\nline2\nline3\n");
        Arrange(margin);
        editor.TextArea.Caret.Line = 1;
        margin.Update([new CodeFixEntry(2, "fix", CodeFixSeverity.Warning, "fix-key")]);
        Assert.Empty(margin.ResolveCandidatesForClick(PointOnLine(margin, 2)));

        editor.TextArea.Caret.Line = 2;

        Assert.Single(margin.ResolveCandidatesForClick(PointOnLine(margin, 2)));
    }

    [Theory]
    [InlineData(CodeFixSeverity.Error, "RunPad.Margin.CodeFixErrorBrush")]
    [InlineData(CodeFixSeverity.Warning, "RunPad.Margin.CodeFixWarningBrush")]
    [InlineData(CodeFixSeverity.Info, "RunPad.Margin.DebugArrowBrush")]
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

    // DrawCentered's scale used to be Bounds.Width * 0.72 / 24.0 — once a later change made
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

    // Confirms the click target was never
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

    // Locks in the size correction itself — #82 had shrunk the glyph to 0.6x line
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

    // Live user report: a real Roslyn refactoring title on an empty-string-literal expression (e.g.
    // "Introduce local for '""'" — the FeaturesResources "Introduce local for '{0}'" format string
    // with {0} = the literal source text "") renders as an unreadable run of quote glyphs in the
    // default proportional UI font. RunPad doesn't reformat fix.Title itself — a monospace font is
    // the fix, so adjacent quote characters stay visually distinct (fixed width) regardless of what
    // ends up embedded in a future title, not just this one reported case.
    [AvaloniaFact]
    public void BuildMenuItem_Should_UseAMonospaceFont_ForBetterQuoteGlyphLegibility()
    {
        var (_, _, margin) = CreateEditor("x\n");

        var item = margin.BuildMenuItem(new CodeFixEntry(1, "Introduce local for '\"\"'", CodeFixSeverity.Info, "fix-key"));

        Assert.Equal(new FontFamily("monospace"), item.FontFamily);
    }

    // The quick-fix MenuFlyout used to build each MenuItem with only a Header, no icon
    // at all. TestAppBuilder deliberately doesn't merge RunPad's own Icons.axaml (real UI tests
    // construct their own view models instead of depending on the full app composition root), so this
    // merges it in directly — the same avares:// URI App.axaml itself uses — just for this one
    // assertion, and removes it again afterward so it can't leak into other tests sharing this
    // process's one Application instance.
    [AvaloniaFact]
    public void BuildMenuItem_Should_SetTheHammerIcon_ForAGenericDiagnosticDrivenFix()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var include = new ResourceInclude(new Uri("avares://RunPad.Runtime.UI.Tests/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(include);
        try
        {
            // DiagnosticId set, title doesn't match any of the using-directive/create-implement/
            // conversion keyword heuristics — falls through to the plain "Fix" category (hammer).
            var item = margin.BuildMenuItem(new CodeFixEntry(1, "Remove unnecessary cast", CodeFixSeverity.Info, "fix-key", DiagnosticId: "IDE0004"));

            var icon = Assert.IsType<PathIcon>(item.Icon);
            Assert.NotNull(icon.Data);
            Assert.Equal("Remove unnecessary cast", item.Header);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(include);
        }
    }

    [AvaloniaFact]
    public void BuildMenuItem_Should_SetTheWrenchIcon_ForAConversionTitle()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var include = new ResourceInclude(new Uri("avares://RunPad.Runtime.UI.Tests/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(include);
        try
        {
            var item = margin.BuildMenuItem(new CodeFixEntry(1, "Convert to interpolated string", CodeFixSeverity.Info, "fix-key"));

            Application.Current.TryGetResource("Icon.Wrench", null, out var wrenchGeo);
            var icon = Assert.IsType<PathIcon>(item.Icon);
            Assert.Same(wrenchGeo, icon.Data);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(include);
        }
    }

    [AvaloniaFact]
    public void BuildMenuItem_Should_SetTheBroomIcon_ForARefactoringTitle()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var include = new ResourceInclude(new Uri("avares://RunPad.Runtime.UI.Tests/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(include);
        try
        {
            var item = margin.BuildMenuItem(new CodeFixEntry(1, "Extract method", CodeFixSeverity.Info, "fix-key"));

            Application.Current.TryGetResource("Icon.Broom", null, out var broomGeo);
            var icon = Assert.IsType<PathIcon>(item.Icon);
            Assert.Same(broomGeo, icon.Data);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(include);
        }
    }

    // CodeFixCategory is internal — Theory data can't carry it directly in a public method signature
    // (CS0051), so the expected category travels as its own name and is parsed back inside the test.
    [Theory]
    [InlineData("using System.Linq;", nameof(CodeFixCategory.UsingDirective))]
    [InlineData("using static System.Math;", nameof(CodeFixCategory.UsingDirective))]
    [InlineData("Generate constructor 'Foo(int)'", nameof(CodeFixCategory.CreateOrImplement))]
    [InlineData("Create class 'Foo'", nameof(CodeFixCategory.CreateOrImplement))]
    [InlineData("Implement interface 'IFoo'", nameof(CodeFixCategory.CreateOrImplement))]
    [InlineData("Convert to interpolated string", nameof(CodeFixCategory.Conversion))]
    [InlineData("Simplify LINQ expression", nameof(CodeFixCategory.Conversion))]
    [InlineData("Use expression body for method", nameof(CodeFixCategory.Conversion))]
    public void Categorize_Should_RecognizeTheKeywordedCategories_RegardlessOfDiagnosticId(string title, string expectedCategoryName)
    {
        var expected = Enum.Parse<CodeFixCategory>(expectedCategoryName);
        Assert.Equal(expected, CodeFixMargin.Categorize(new CodeFixEntry(1, title, CodeFixSeverity.Info, "key")));
        Assert.Equal(expected, CodeFixMargin.Categorize(new CodeFixEntry(1, title, CodeFixSeverity.Info, "key", DiagnosticId: "CS0246")));
    }

    [Fact]
    public void Categorize_Should_ReturnFix_ForANonKeywordTitle_WithADiagnosticId()
    {
        var fix = new CodeFixEntry(1, "Remove unnecessary cast", CodeFixSeverity.Info, "key", DiagnosticId: "IDE0004");

        Assert.Equal(CodeFixCategory.Fix, CodeFixMargin.Categorize(fix));
    }

    [Fact]
    public void Categorize_Should_ReturnRefactoring_ForANonKeywordTitle_WithNoDiagnosticId()
    {
        var fix = new CodeFixEntry(1, "Extract method", CodeFixSeverity.Info, "key");

        Assert.Equal(CodeFixCategory.Refactoring, CodeFixMargin.Categorize(fix));
    }

    [Theory]
    [InlineData(nameof(CodeFixCategory.UsingDirective), "Icon.Hammer")]
    [InlineData(nameof(CodeFixCategory.CreateOrImplement), "Icon.Hammer")]
    [InlineData(nameof(CodeFixCategory.Fix), "Icon.Hammer")]
    [InlineData(nameof(CodeFixCategory.Conversion), "Icon.Wrench")]
    [InlineData(nameof(CodeFixCategory.Refactoring), "Icon.Broom")]
    public void IconKeyForCategory_Should_MapEachCategoryToItsOwnIconResourceKey(string categoryName, string expectedKey)
    {
        var category = Enum.Parse<CodeFixCategory>(categoryName);
        Assert.Equal(expectedKey, CodeFixMargin.IconKeyForCategory(category));
    }

    [AvaloniaFact]
    public void BuildMenuItems_Should_NotAddASeparator_WhenAllCandidatesAreTheSameCategory()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var candidates = new[]
        {
            new CodeFixEntry(1, "using System;", CodeFixSeverity.Info, "k1"),
            new CodeFixEntry(1, "using System.Linq;", CodeFixSeverity.Info, "k2"),
        };

        var items = margin.BuildMenuItems(candidates);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.IsType<MenuItem>(i));
    }

    [AvaloniaFact]
    public void BuildMenuItems_Should_GroupByCategory_WithASeparatorBetweenNonEmptyGroups_InMenuGroupOrder()
    {
        var (_, _, margin) = CreateEditor("x\n");
        // Deliberately listed out of the expected menu order (refactoring first, using-directive
        // second) — BuildMenuItems is what's responsible for reordering by category, not callers.
        var candidates = new[]
        {
            new CodeFixEntry(1, "Extract method", CodeFixSeverity.Info, "refactor-key"),
            new CodeFixEntry(1, "using System;", CodeFixSeverity.Info, "using-key"),
        };

        var items = margin.BuildMenuItems(candidates);

        Assert.Equal(3, items.Count); // using-directive item, separator, refactoring item
        var usingItem = Assert.IsType<MenuItem>(items[0]);
        Assert.Equal("using System;", usingItem.Header);
        Assert.IsType<Separator>(items[1]);
        var refactorItem = Assert.IsType<MenuItem>(items[2]);
        Assert.Equal("Extract method", refactorItem.Header);
    }

    [AvaloniaFact]
    public void BuildMenuItems_Should_PreserveOriginalOrder_WithinEachGroup()
    {
        var (_, _, margin) = CreateEditor("x\n");
        var candidates = new[]
        {
            new CodeFixEntry(1, "using System.Linq;", CodeFixSeverity.Info, "second-listed"),
            new CodeFixEntry(1, "using System;", CodeFixSeverity.Info, "first-listed"),
        };

        var items = margin.BuildMenuItems(candidates);

        Assert.Equal("using System.Linq;", Assert.IsType<MenuItem>(items[0]).Header);
        Assert.Equal("using System;", Assert.IsType<MenuItem>(items[1]).Header);
    }

    // Apply used to set
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

    // Sanity check on the three converted-from-SVG geometries themselves (Icon.Hammer
    // was replaced, Icon.Broom/Icon.Wrench are new): each should parse to a non-degenerate shape
    // roughly matching its source SVG's own viewBox, not e.g. a single stray point from a typo'd
    // coordinate. Doesn't (can't, headless) confirm the glyph LOOKS like a hammer/broom/wrench —
    // only that the geometry data is sane.
    [Theory]
    [InlineData("Icon.Hammer", 32, 32)]
    [InlineData("Icon.Broom", 32, 32)]
    [InlineData("Icon.Wrench", 24, 24)]
    public void CodeFixIcons_Should_ParseToANonDegenerateShape_WithinTheirSourceViewBox(string key, double maxWidth, double maxHeight)
    {
        var include = new ResourceInclude(new Uri("avares://RunPad.Runtime.UI.Tests/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(include);
        try
        {
            Application.Current.TryGetResource(key, null, out var res);
            var geo = Assert.IsType<StreamGeometry>(res);

            Assert.True(geo.Bounds.Width > 1, $"{key}'s bounds are suspiciously narrow: {geo.Bounds}");
            Assert.True(geo.Bounds.Height > 1, $"{key}'s bounds are suspiciously short: {geo.Bounds}");
            Assert.True(geo.Bounds.Width <= maxWidth, $"{key}'s bounds are wider than its source viewBox: {geo.Bounds}");
            Assert.True(geo.Bounds.Height <= maxHeight, $"{key}'s bounds are taller than its source viewBox: {geo.Bounds}");
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(include);
        }
    }

    private static void Arrange(CodeFixMargin margin)
    {
        margin.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        margin.Arrange(new Rect(margin.DesiredSize));
    }

    // Same Y-coordinate computation as ResolveLineFromPoint_Should_ResolveTheSameLine_
    // RegardlessOfXWithinTheMargin above — X doesn't matter for line resolution (that test's own
    // point), so this always aims at the horizontal center of the margin's own column.
    private static Point PointOnLine(CodeFixMargin margin, int line)
    {
        var textView = margin.TextView;
        var vl = textView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == line);
        var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineTop)
            - textView.VerticalOffset;
        return new Point(margin.Bounds.Width / 2, yTop + vl.Height / 2);
    }
}
