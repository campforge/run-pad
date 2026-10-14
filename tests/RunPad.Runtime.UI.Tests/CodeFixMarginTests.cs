using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // Regression guard for the alignment half of the gutter fix: before the fix, MeasureOverride returned
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

    // Locks in the size correction itself — the first size fix had shrunk the glyph to 0.6x line
    // height (close to its original pre-alignment size), which read as too small to comfortably aim at
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

    // ── The bulb's square click target ───────────────────────────────────────

    private static CodeFixMargin MarginWithFixOnCaretLine(CodeTextEditor editor, CodeFixMargin margin, int line)
    {
        editor.TextArea.Caret.Line = line;
        margin.Update([new CodeFixEntry(line, "fix", CodeFixSeverity.Error, "fix-key")]);
        Settle(editor);
        return margin;
    }

    // Layout, then the margin's deferred re-measure, then layout again.
    private static void Settle(CodeTextEditor editor)
    {
        editor.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        editor.UpdateLayout();
    }

    private static void ScrollTo(CodeTextEditor editor, double offset)
    {
        // TextEditor.ScrollToVerticalOffset does nothing in this AvaloniaEdit version: scroll its viewer.
        editor.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, offset);
        Settle(editor);
    }

    private static string Lines(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"line{i}\n"));

    // Square, larger than the glyph, inside the margin's own column, centered on the glyph.
    [AvaloniaFact]
    public void HitTarget_Should_BeASquare_CenteredOnTheGlyph_InsideItsColumn()
    {
        var (_, editor, margin) = CreateEditor(Lines(5));
        MarginWithFixOnCaretLine(editor, margin, 2);

        var target = Assert.NotNull(margin.HitTarget());
        var center = PointOnLine(margin, 2);
        var vl = margin.TextView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == 2);
        var (scale, x, y) = margin.ComputeIconTransform(vl.Height, center.Y - vl.Height / 2);
        var glyph = 24 * scale;

        Assert.Equal(target.Width, target.Height, 6);
        Assert.True(target.Width > glyph, $"target {target.Width}, glyph {glyph}");
        Assert.Equal(Math.Min(CodeFixMargin.MinimumHitTargetSize, margin.Bounds.Width), target.Width, 6);
        Assert.True(target.X >= 0 && target.Right <= margin.Bounds.Width + 1e-9);
        Assert.Equal(x + glyph / 2, target.Center.X, 6);
        Assert.Equal(y + glyph / 2, target.Center.Y, 6);
    }

    // Anywhere in the square — padding, edges, corners — does what a click on the glyph does.
    [AvaloniaFact]
    public void AClickAnywhereInTheSquare_Should_OpenTheSameFixes_AsTheCenter()
    {
        var (_, editor, margin) = CreateEditor(Lines(5));
        MarginWithFixOnCaretLine(editor, margin, 3);
        var t = margin.HitTarget()!.Value;
        const double inset = 0.5;
        var center = margin.ResolveCandidatesForClick(t.Center);
        Assert.Single(center);

        foreach (var point in new[]
                 {
                     new Point(t.X + inset, t.Y + inset), new Point(t.Right - inset, t.Y + inset),
                     new Point(t.X + inset, t.Bottom - inset), new Point(t.Right - inset, t.Bottom - inset),
                     new Point(t.Center.X, t.Y + inset), new Point(t.Center.X, t.Bottom - inset),
                     new Point(t.X + inset, t.Center.Y), new Point(t.Right - inset, t.Center.Y),
                 })
            Assert.Equal(center, margin.ResolveCandidatesForClick(point));
    }

    // Outside the square nothing happens — beside it on the same line (a wide column: four-digit line
    // numbers) and above/below it.
    [AvaloniaFact]
    public void AClickOutsideTheSquare_Should_NotOpenAnyFix()
    {
        var (_, editor, margin) = CreateEditor(Lines(1500));
        MarginWithFixOnCaretLine(editor, margin, 3);
        var t = margin.HitTarget()!.Value;
        Assert.True(margin.Bounds.Width > t.Width + 4, $"column {margin.Bounds.Width}, target {t.Width}");

        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.X - 1, t.Center.Y)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Right + 1, t.Center.Y)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Center.X, t.Y - 1)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Center.X, t.Bottom + 1)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(0.5, t.Center.Y)));
    }

    // A line shorter than the square: the part of the square over the next line's row still opens
    // the caret line's fixes, not that line's.
    [AvaloniaFact]
    public void TheSquare_Should_ReachPastAShortLine_AndStillOpenTheCaretLinesFixes()
    {
        var (_, editor, margin) = CreateEditor(Lines(5));
        MarginWithFixOnCaretLine(editor, margin, 2);
        margin.Update([.. margin.Fixes, new CodeFixEntry(3, "other", CodeFixSeverity.Warning, "other-key")]);
        var t = margin.HitTarget()!.Value;
        var lineHeight = margin.TextView.DefaultLineHeight;
        Assert.True(lineHeight < t.Height, $"line {lineHeight}, target {t.Height}");

        var belowTheLine = new Point(t.Center.X, t.Bottom - 0.5);
        Assert.Equal(3, margin.ResolveLineFromPoint(belowTheLine));
        Assert.Equal(["fix"], margin.ResolveCandidatesForClick(belowTheLine).Select(f => f.Title));
    }

    [AvaloniaFact]
    public void WithoutAFixOnTheCaretLine_Should_HaveNoTarget()
    {
        var (_, editor, margin) = CreateEditor(Lines(5));
        Arrange(margin);
        Assert.Null(margin.HitTarget()); // no fixes at all

        editor.TextArea.Caret.Line = 1;
        margin.Update([new CodeFixEntry(2, "fix", CodeFixSeverity.Warning, "fix-key")]);
        Settle(editor);
        Assert.Null(margin.HitTarget());
        Assert.Empty(margin.ResolveCandidatesForClick(PointOnLine(margin, 2)));
    }

    // The target moves with the caret, and with scrolling; a caret line scrolled out of view has none.
    [AvaloniaFact]
    public void TheTarget_Should_FollowTheCaret_AndScrolling()
    {
        var (window, editor, margin) = CreateEditor(Lines(300));
        window.Height = 300; // shorter than the document, so it scrolls
        MarginWithFixOnCaretLine(editor, margin, 2);
        var before = margin.HitTarget()!.Value;

        editor.TextArea.Caret.Line = 3;
        margin.Update([new CodeFixEntry(3, "fix", CodeFixSeverity.Error, "fix-key")]);
        Settle(editor);
        var moved = margin.HitTarget()!.Value;
        Assert.Equal(PointOnLine(margin, 3).Y, moved.Center.Y, 6);
        Assert.True(moved.Center.Y > before.Center.Y);

        ScrollTo(editor, margin.TextView.DefaultLineHeight * 1.5);
        var scrolled = margin.HitTarget()!.Value;
        Assert.Equal(PointOnLine(margin, 3).Y, scrolled.Center.Y, 6);
        Assert.True(scrolled.Center.Y < moved.Center.Y);
        Assert.Single(margin.ResolveCandidatesForClick(scrolled.Center));

        ScrollTo(editor, margin.TextView.DefaultLineHeight * 200);
        Assert.Null(margin.HitTarget());
    }

    // A wrapped caret line: the glyph and its square sit at the middle of the whole wrapped line.
    [AvaloniaFact]
    public void OnAWrappedLine_TheSquare_Should_StayCenteredOnTheGlyph()
    {
        var (window, editor, margin) = CreateEditor("short\n" + string.Join(' ', Enumerable.Repeat("wrapped", 80)) + "\nshort\n");
        window.Width = 300;
        editor.WordWrap = true;
        MarginWithFixOnCaretLine(editor, margin, 2);
        var vl = margin.TextView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == 2);
        Assert.True(vl.TextLines.Count > 1);

        var t = margin.HitTarget()!.Value;
        var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineTop)
                   - margin.TextView.VerticalOffset;
        var (scale, x, y) = margin.ComputeIconTransform(vl.Height, yTop);
        Assert.Equal(y + 12 * scale, t.Center.Y, 6);
        Assert.Equal(x + 12 * scale, t.Center.X, 6);
        Assert.Single(margin.ResolveCandidatesForClick(new Point(t.X + 0.5, t.Y + 0.5)));
    }

    // A large font makes the glyph bigger than 24 px: the square grows with it, still inside the column.
    [AvaloniaFact]
    public void WithALargeFont_TheSquare_Should_StayLargerThanTheGlyph()
    {
        var (_, editor, margin) = CreateEditor(Lines(5));
        editor.FontSize = 48;
        MarginWithFixOnCaretLine(editor, margin, 2);

        var t = margin.HitTarget()!.Value;
        var glyph = 24 * margin.ComputeIconTransform(20, 0).Scale;
        Assert.True(glyph > CodeFixMargin.MinimumHitTargetSize);
        Assert.True(t.Width > glyph, $"target {t.Width}, glyph {glyph}");
        Assert.True(t.Right <= margin.Bounds.Width + 1e-9);
        Assert.Equal(t.Width, t.Height, 6);
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
