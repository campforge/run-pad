using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The quick-fix lightbulb margin: severity colors, the compact column as wide as the bulb's square,
// the square click target (through the helpers and through real pointer events) and the menu.
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

    // The column is exactly as wide as the bulb's square — a compact gutter with the glyph next to the
    // line numbers — whatever the line count (it used to follow the line-number column's width), and
    // the glyph's size follows the line height, not the column.
    [AvaloniaFact]
    public void TheColumn_Should_BeAsWideAsTheSquare_WhateverTheLineCount()
    {
        var (_, shortEditor, shortMargin) = CreateEditor("line\n");
        var (_, longEditor, longMargin) = CreateEditor(string.Concat(Enumerable.Repeat("line\n", 1500)));
        MarginWithFixOnCaretLine(shortEditor, shortMargin, 1);
        MarginWithFixOnCaretLine(longEditor, longMargin, 1);

        // layout rounds the column up to whole pixels; the square itself is exactly the computed side
        var side = shortMargin.HitTargetSide();
        Assert.True(side >= CodeFixMargin.MinimumHitTargetSize);
        Assert.InRange(shortMargin.Bounds.Width, side, side + 1);
        Assert.Equal(shortMargin.Bounds.Width, longMargin.Bounds.Width, 3);
        Assert.Equal(side, shortMargin.HitTarget()!.Value.Width, 3);
        Assert.Equal(shortMargin.ComputeIconTransform(lineH: 20, yTop: 0).Scale,
            longMargin.ComputeIconTransform(lineH: 20, yTop: 0).Scale, 6);
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

        Assert.Equal(MonospaceFont.Default, item.FontFamily);
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
        editor.ScrollToVerticalOffset(offset);
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
        Assert.Equal(Math.Min(margin.HitTargetSide(), margin.Bounds.Width), target.Width, 6);
        // the glyph plus the padding on each side
        Assert.Equal(Math.Max(CodeFixMargin.MinimumHitTargetSize, glyph + 2 * CodeFixMargin.HitTargetPadding), target.Width, 6);
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

    // Outside the square — above and below it, in this column — nothing happens. (Beside it there is no
    // room: the column is as wide as the square.)
    [AvaloniaFact]
    public void AClickOutsideTheSquare_Should_NotOpenAnyFix()
    {
        var (_, editor, margin) = CreateEditor(Lines(1500));
        MarginWithFixOnCaretLine(editor, margin, 3);
        var t = margin.HitTarget()!.Value;

        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Center.X, t.Y - 1)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Center.X, t.Bottom + 1)));
        Assert.Empty(margin.ResolveCandidatesForClick(new Point(t.Center.X, t.Bottom + 3 * margin.TextView.DefaultLineHeight)));
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

    // A wrapped caret line: the glyph — its actual drawn shape, the app's own icon — and its square sit
    // on the line's first rendered row, the row the code starts on, not in the middle of the whole
    // multi-row block (where the bulb ended up below the line's start). Several font sizes: the
    // position follows the row, with no fixed correction.
    [AvaloniaTheory]
    [InlineData(9.0)]
    [InlineData(14.0)]
    [InlineData(28.0)]
    public void OnAWrappedLine_TheGlyph_Should_BeCenteredOnTheFirstTextRow(double fontSize)
    {
        // avares://RunPad/ resolves only once the RunPad assembly is loaded
        _ = typeof(RunPad.App).Assembly;
        var icons = new global::Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://RunPad/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(icons);
        try
        {
            var (window, editor, margin) = CreateEditor("short\n" + string.Join(' ', Enumerable.Repeat("wrapped", 80)) + "\nshort\n");
            window.Width = 300;
            editor.FontSize = fontSize;
            editor.WordWrap = true;
            MarginWithFixOnCaretLine(editor, margin, 2);
            var vl = margin.TextView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == 2);
            Assert.True(vl.TextLines.Count > 2, $"{vl.TextLines.Count} rows");

            var offset = margin.TextView.VerticalOffset;
            var rowTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineTop) - offset;
            var rowBottom = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - offset;
            var rowCenter = (rowTop + rowBottom) / 2;
            var blockCenter = rowTop + vl.Height / 2;

            var glyph = Assert.NotNull(margin.GlyphBounds());
            Assert.InRange(glyph.Center.Y, rowCenter - 0.5, rowCenter + 0.5);
            Assert.True(glyph.Top >= rowTop - 0.5 && glyph.Bottom <= rowBottom + 0.5,
                $"glyph {glyph.Top}..{glyph.Bottom}, row {rowTop}..{rowBottom}");
            Assert.True(blockCenter - glyph.Center.Y > (rowBottom - rowTop) / 2, "not centered on the whole block");

            var t = margin.HitTarget()!.Value;
            Assert.Equal(rowCenter, t.Center.Y, 3);
            Assert.Equal(glyph.Center.X, t.Center.X, 3);
            Assert.Single(margin.ResolveCandidatesForClick(new Point(t.X + 0.5, t.Y + 0.5)));
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(icons);
        }
    }

    // The same through the editor's own zoom — real Cmd/Ctrl+wheel events over the text — in both
    // directions: after each step the glyph is still centered on the wrapped line's first row.
    [AvaloniaFact]
    public void AfterZoomingWithTheWheel_TheGlyph_Should_StayOnTheWrappedLinesFirstRow()
    {
        _ = typeof(RunPad.App).Assembly; // avares://RunPad/ resolves only once the RunPad assembly is loaded
        var icons = new global::Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://RunPad/"))
        {
            Source = new Uri("avares://RunPad/Resources/Icons.axaml"),
        };
        Application.Current!.Resources.MergedDictionaries.Add(icons);
        try
        {
            var (window, editor, margin) = CreateEditor("short\n" + string.Join(' ', Enumerable.Repeat("wrapped", 80)) + "\nshort\n");
            window.Width = 300;
            editor.WordWrap = true;
            MarginWithFixOnCaretLine(editor, margin, 2);
            var command = editor.CommandModifier == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
            var overText = editor.TextArea.TextView.TranslatePoint(new Point(40, 5), window)!.Value;
            var startSize = editor.FontSize;

            foreach (var (steps, delta) in new[] { (6, 1.0), (10, -1.0) })
            {
                for (var i = 0; i < steps; i++) window.MouseWheel(overText, new Vector(0, delta), command);
                Settle(editor);

                var vl = margin.TextView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == 2);
                Assert.True(vl.TextLines.Count > 1, $"font {editor.FontSize}: {vl.TextLines.Count} rows");
                var offset = margin.TextView.VerticalOffset;
                var rowTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineTop) - offset;
                var rowBottom = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - offset;
                var glyph = Assert.NotNull(margin.GlyphBounds());
                Assert.InRange(glyph.Center.Y, (rowTop + rowBottom) / 2 - 0.5, (rowTop + rowBottom) / 2 + 0.5);
                Assert.Equal((rowTop + rowBottom) / 2, margin.HitTarget()!.Value.Center.Y, 3);
            }
            Assert.Equal(startSize - 4, editor.FontSize); // the wheel really zoomed: +6, then -10
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(icons);
        }
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

    // ── Real pointer events ───────────────────────────────────────────────────
    //
    // Through the window's input pipeline (hit testing, routing, capture), not the helpers above: a
    // click on the square's transparent padding used to miss the margin — it draws only the glyph —
    // and moved the caret in the text area instead.

    private static (Window Window, CodeTextEditor Editor, CodeFixMargin Margin, Rect Target) BulbOnLine2()
    {
        var editor = new CodeTextEditor { Document = new TextDocument("line1\nvar value = \"sample\";\nline3\n") };
        var window = new Window { Content = editor, Width = 600, Height = 300 };
        window.Show();
        var margin = editor.TextArea.LeftMargins.OfType<CodeFixMargin>().Single();
        editor.TextArea.Caret.Line = 2;
        editor.TextArea.Caret.Column = 5;
        margin.Update([new CodeFixEntry(2, "fix", CodeFixSeverity.Warning, "fix-key")]);
        Settle(editor);
        // Input hit testing uses the rendered scene: render a frame first, as a real window would.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, editor, margin, margin.HitTarget()!.Value);
    }

    private static void Click(Window window, CodeFixMargin margin, Point inMargin, Point? releaseInMargin = null)
    {
        window.MouseDown(margin.TranslatePoint(inMargin, window)!.Value, MouseButton.Left);
        window.MouseUp(margin.TranslatePoint(releaseInMargin ?? inMargin, window)!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(0.0, 0.0)]    // the glyph's center
    [InlineData(0.0, 0.45)]   // padding below the glyph (the reported case)
    [InlineData(0.0, -0.45)]  // padding above
    [InlineData(-0.45, 0.45)] // corners
    [InlineData(0.45, -0.45)]
    public void AClickInTheSquare_Should_OpenTheMenu_AndLeaveTheCaret(double dx, double dy)
    {
        var (window, editor, margin, t) = BulbOnLine2();
        var point = new Point(t.Center.X + t.Width * dx, t.Center.Y + t.Height * dy);

        Assert.Same(margin, window.InputHitTest(margin.TranslatePoint(point, window)!.Value));
        Click(window, margin, point);

        Assert.True(margin.OpenMenu?.IsOpen, "the menu is open after the release");
        Assert.Equal(["fix"], margin.OpenMenu!.Items.OfType<MenuItem>().Select(i => i.Header));
        Assert.Equal((2, 5), (editor.TextArea.Caret.Line, editor.TextArea.Caret.Column));
    }

    // The menu is anchored to the bulb's square and opens right below it — not "at the pointer", which
    // depends on the window's last known pointer position and, without one, put the native menu at the
    // top-left corner of the screen.
    [AvaloniaFact]
    public void TheMenu_Should_OpenRightBelowTheSquare()
    {
        var (window, _, margin, t) = BulbOnLine2();

        Click(window, margin, new Point(t.Center.X, t.Bottom - 1));

        var menu = margin.OpenMenu!;
        Assert.True(menu.IsOpen);
        Assert.Same(margin, menu.Target);
        Assert.Equal(t, menu.Popup.PlacementRect);
        Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Popup.Placement);
        var presenter = Assert.IsAssignableFrom<Control>(menu.Popup.Child);
        var topLeft = presenter.TranslatePoint(default, margin)!.Value;
        // popups are placed on whole pixels
        Assert.InRange(topLeft.X, t.X - 1, t.X + 1);
        Assert.InRange(topLeft.Y, t.Bottom - 1, t.Bottom + 1);
    }

    // Beside the square (a wide column) or on another line's row: no menu, and the click stays in the
    // margin instead of reaching the text area.
    [AvaloniaFact]
    public void AClickInTheColumn_OutsideTheSquare_Should_DoNothing()
    {
        var (window, editor, margin, t) = BulbOnLine2();
        var lineHeight = margin.TextView.DefaultLineHeight;

        Click(window, margin, new Point(t.Center.X, t.Bottom + lineHeight)); // the next lines' rows
        Click(window, margin, new Point(t.Center.X, t.Top - 2));

        Assert.Null(margin.OpenMenu);
        Assert.Equal((2, 5), (editor.TextArea.Caret.Line, editor.TextArea.Caret.Column));
    }

    // Pressed on the bulb, released elsewhere: not a click.
    [AvaloniaFact]
    public void PressingOnTheBulb_AndReleasingOutside_Should_NotOpenTheMenu()
    {
        var (window, _, margin, t) = BulbOnLine2();

        Click(window, margin, t.Center, releaseInMargin: new Point(t.Center.X, t.Bottom + 30));

        Assert.Null(margin.OpenMenu);
    }

    // Picking an entry of the opened menu applies that fix (the same routing as before).
    [AvaloniaFact]
    public void AnEntryOfTheMenuOpenedByAClick_Should_ApplyItsFix()
    {
        var (window, _, margin, t) = BulbOnLine2();
        CodeFixEntry? applied = null;
        margin.FixApplyRequested += fix => applied = fix;

        Click(window, margin, new Point(t.Center.X, t.Bottom - 1));
        margin.OpenMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("fix-key", applied?.Key);
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
