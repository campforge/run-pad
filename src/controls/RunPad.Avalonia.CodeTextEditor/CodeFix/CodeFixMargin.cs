using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

// Live user request: group the lightbulb's dropdown menu by kind (using-directive
// fixes separately from create/implement variants), and give fixes/refactorings/conversions their
// own icon instead of one hammer for everything. Neither CodeFixEntry nor the upstream
// RunPad.Contracts.IntelliSense.ICodeFixInfo it mirrors carries a "kind" at all — only
// Title/DiagnosticId — so CodeFixMargin.Categorize below is built from those two signals:
// DiagnosticId != null is already a real, existing distinction (RoslynCodeFixEngine only ever sets
// it for ComputeFixesAsync's diagnostic-driven results, never for ComputeRefactoringsAsync's
// caret-triggered ones — CodeFixEntry's own doc comment), and beyond that a Title keyword heuristic.
// Roslyn's own built-in fix/refactoring catalog is a couple hundred MEF-discovered providers
// (RoslynCodeFixEngine's own doc comment) — too broad to map exhaustively by exact title string —
// so this covers the three groupings the user explicitly asked for (using-directives, create/
// implement, conversions/minor edits) and falls back to the existing DiagnosticId split (Fix vs
// Refactoring) for everything else a keyword doesn't recognize.
internal enum CodeFixCategory
{
    UsingDirective,
    CreateOrImplement,
    Conversion,
    Fix,
    Refactoring,
}

// Lightbulb gutter margin: shows an icon on the CURRENT (caret) line when it has an available quick
// fix, and applies it on click. Real Roslyn CodeFixProviders (RoslynCodeFixEngine) routinely offer
// several candidates for one line (e.g. multiple "using X;" namespaces for the same unresolved
// type) — a single click applies the only fix directly, or opens a small picker menu when there's
// more than one.
//
// User request 2026-07-21: only the caret's own line ever shows the icon, VS/Rider-
// style, instead of every line in the file that currently has a fix all being lit up simultaneously
// — the margin used to render one icon per fix-bearing line regardless of where the caret was,
// which got noisy fast on a script with several unrelated diagnostics open at once.
public class CodeFixMargin : AbstractMargin
{
    static CodeFixMargin()
    {
        FocusableProperty.OverrideDefaultValue(typeof(CodeFixMargin), false);
    }

    private readonly CodeTextEditor _editor;
    private IReadOnlyList<CodeFixEntry> _fixes = [];
    private StreamGeometry? _bulbGeo;

    // Was a single _hammerGeo field — now three possible icons (Categorize/IconKeyForCategory
    // below), cached by resource key instead of one field per icon.
    private readonly Dictionary<string, StreamGeometry?> _menuIconCache = new();

    // Tracks the caret's own document line so PositionChanged (fired on every column move too, not
    // just line changes — e.g. Left/Right arrow within the same line) only triggers a repaint when
    // the line the icon could be drawn on actually changed, not on every single caret move.
    private int _caretLine;

    // Raised when the user
    // clicks a specific menu entry. This control has no notion of Roslyn/IIntelliSenseService (see
    // CodeFixEntry's own doc comment) — materializing the fix's actual text (and routing any usings
    // it wants to import) is entirely the click-time responsibility of whoever owns that (forwarded
    // up via CodeTextEditor.FixApplyRequested).
    public event Action<CodeFixEntry>? FixApplyRequested;

    public CodeFixMargin(CodeTextEditor editor)
    {
        _editor = editor;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;

        _caretLine = _editor.TextArea.Caret.Line;
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            var line = _editor.TextArea.Caret.Line;
            if (line == _caretLine) return;
            _caretLine = line;
            InvalidateVisual();
        };
    }

    public void Update(IReadOnlyList<CodeFixEntry> fixes)
    {
        _fixes = fixes;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!TextView.VisualLinesValid || TextView.VisualLines.Count == 0 || _fixes.Count == 0) return;

        var worst = WorstSeverityOnLine(_caretLine);
        if (worst is null) return;

        var vl = TextView.VisualLines.FirstOrDefault(v => v.FirstDocumentLine.LineNumber == _caretLine);
        if (vl is null) return; // caret line currently scrolled out of view — nothing to draw

        _bulbGeo ??= LoadResource<StreamGeometry>("Icon.Lightbulb");
        if (_bulbGeo is null) return;

        var brush = LoadThemedResource<IBrush>(SeverityBrushKey(worst.Value));
        var lineH = vl.Height;
        var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop)
                      - TextView.VerticalOffset;
        DrawCentered(context, _bulbGeo, brush, yTop, lineH);
    }

    // Test-only observability — same rationale as every other "internal test hook" this session: lets
    // a test find a specific candidate by title/line without needing a live paint pass.
    internal IReadOnlyList<CodeFixEntry> Fixes => _fixes;

    // Several fixes can share one line (e.g. multiple "using X;" candidates for the
    // same unresolved type) at different severities — the icon shows whichever is worse, same
    // priority order BreakPointMargin's own debug-arrow/breakpoint overlap uses (an error is more
    // worth the user's attention than a warning sitting on the same line). Internal (not private) +
    // InternalsVisibleTo (see the csproj), same pattern as CSharpReferenceService.IsManagedAssembly
    // — so this and SeverityBrushKey can be tested directly without a live TextView.
    internal CodeFixSeverity? WorstSeverityOnLine(int lineNo)
    {
        CodeFixSeverity? worst = null;
        foreach (var f in _fixes)
        {
            if (f.Line != lineNo) continue;
            if (worst is null || f.Severity > worst) worst = f.Severity;
        }
        return worst;
    }

    internal static string SeverityBrushKey(CodeFixSeverity severity) => severity switch
    {
        CodeFixSeverity.Error => "RunPad.Margin.CodeFixErrorBrush",
        CodeFixSeverity.Warning => "RunPad.Margin.CodeFixWarningBrush",
        _ => "RunPad.Margin.DebugArrowBrush",
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var candidates = ResolveCandidatesForClick(e.GetPosition(this));
        if (candidates.Count == 0) return;

        // Always go through the menu, even for a single candidate — VS/Rider-style, so the lightbulb
        // never applies anything sight-unseen and there's a consistent place to add more entries later
        // (Suppress, Fix all in file/project, ...) without a special case for "exactly one fix".
        var menu = new MenuFlyout();
        foreach (var item in BuildMenuItems(candidates))
            menu.Items.Add(item);
        // showAtPointer: true — anchors the flyout to where the user actually clicked (the lightbulb
        // icon on this specific line) instead of PopupFlyoutBase's default placement relative to the
        // whole margin control, which spans the full editor height and made the menu appear pinned to
        // the bottom-left corner regardless of which line's lightbulb was clicked.
        menu.ShowAt(this, true);

        e.Handled = true;
    }

    // Group order the dropdown menu presents categories in — using-directive fixes
    // first (the single most common/expected quick fix), then create/implement, then everything
    // else diagnostic-driven, then conversions, refactorings last (caret-triggered, no diagnostic —
    // the "furthest" from what actually flagged this line).
    private static readonly CodeFixCategory[] MenuGroupOrder =
    [
        CodeFixCategory.UsingDirective,
        CodeFixCategory.CreateOrImplement,
        CodeFixCategory.Fix,
        CodeFixCategory.Conversion,
        CodeFixCategory.Refactoring,
    ];

    // The grouping half of OnPointerPressed, factored out (same "internal test hook"
    // pattern as ResolveCandidatesForClick/WorstSeverityOnLine) so it can be asserted directly
    // without driving a real PointerPressed/MenuFlyout interaction. A Separator only ever appears
    // BETWEEN two non-empty groups — a single-category candidate list (the common case: one line,
    // one kind of fix) renders exactly as it did before this grouping existed, with no stray
    // separator at either end.
    internal IReadOnlyList<Control> BuildMenuItems(IReadOnlyList<CodeFixEntry> candidates)
    {
        var items = new List<Control>();
        var firstGroup = true;
        foreach (var category in MenuGroupOrder)
        {
            var inGroup = candidates.Where(f => Categorize(f) == category).ToList();
            if (inGroup.Count == 0) continue;

            if (!firstGroup) items.Add(new Separator());
            firstGroup = false;

            foreach (var fix in inGroup)
                items.Add(BuildMenuItem(fix));
        }
        return items;
    }

    // See this file's own CodeFixCategory doc comment for the full reasoning. Roslyn's
    // own AddImport code fix titles are the bare import statement itself (e.g. "using System.Linq;"
    // or "using static System.Math;"), not an "Add ..." sentence — matched by that shape rather than
    // a fixed prefix search.
    internal static CodeFixCategory Categorize(CodeFixEntry fix)
    {
        var title = fix.Title;

        if (title.StartsWith("using ", StringComparison.Ordinal) && title.EndsWith(';'))
            return CodeFixCategory.UsingDirective;
        if (ContainsAnyOrdinal(title, CreateOrImplementKeywords))
            return CodeFixCategory.CreateOrImplement;
        if (ContainsAnyOrdinal(title, ConversionKeywords))
            return CodeFixCategory.Conversion;

        return fix.DiagnosticId is null ? CodeFixCategory.Refactoring : CodeFixCategory.Fix;
    }

    private static readonly string[] CreateOrImplementKeywords = ["Generate", "Create", "Implement"];
    private static readonly string[] ConversionKeywords = ["Convert ", "Simplify ", "Use "];

    private static bool ContainsAnyOrdinal(string title, string[] keywords) =>
        keywords.Any(k => title.Contains(k, StringComparison.Ordinal));

    internal static string IconKeyForCategory(CodeFixCategory category) => category switch
    {
        CodeFixCategory.Conversion => "Icon.Wrench",
        CodeFixCategory.Refactoring => "Icon.Broom",
        _ => "Icon.Hammer",
    };

    // Same "PathIcon per MenuItem" pattern as FileTreeView.axaml's Rename/Delete menu
    // items — this menu is built in code-behind rather than XAML, so it's the same StreamGeometry
    // resource assigned via a fresh PathIcon instance per item (Avalonia visuals can't be shared
    // across multiple items) instead of a XAML <MenuItem.Icon>. The icon now depends
    // on Categorize(fix) instead of always being the hammer. Internal (not private) +
    // InternalsVisibleTo, same pattern as WorstSeverityOnLine, so the icon wiring can be asserted
    // directly without driving a real PointerPressed/MenuFlyout interaction.
    // Live user report: a title like Roslyn's own real "Introduce local for '""'" (FeaturesResources'
    // "Introduce local for '{0}'" format string, {0} filled with the target expression's literal
    // source text — here an empty string literal, i.e. the two characters "") renders, in the
    // default proportional UI font, as a run of quote glyphs packed close enough together to read as
    // an unreadable "\"\"\"\"" blur — the single quotes Roslyn wraps {0} in and the double quotes
    // that ARE {0} become visually indistinguishable at menu font size. RunPad doesn't add or
    // reformat these titles itself (fix.Title reaches here verbatim from Roslyn's own CodeAction.
    // Title) — a monospace font is the general fix: every character gets equal, fixed width, so
    // adjacent quote glyphs stay visually separable regardless of what expression text ends up
    // embedded in a future title. "monospace" (the bare generic keyword, not a specific font name) —
    // same choice, same reason, as EditorSettings.FontFamily's own default: reliably
    // resolves to a real non-ligature monospace font on every platform, unlike a named fallback chain.
    private static readonly FontFamily MenuItemFontFamily = new("monospace");

    internal MenuItem BuildMenuItem(CodeFixEntry fix)
    {
        var iconKey = IconKeyForCategory(Categorize(fix));
        if (!_menuIconCache.TryGetValue(iconKey, out var geo))
        {
            geo = LoadResource<StreamGeometry>(iconKey);
            _menuIconCache[iconKey] = geo;
        }

        var item = new MenuItem { Header = fix.Title, FontFamily = MenuItemFontFamily };
        if (geo is not null)
            item.Icon = new PathIcon { Data = geo, Width = 16, Height = 16 };
        item.Click += (_, _) => Apply(fix);
        return item;
    }

    // A click anywhere across the margin's full (now gutter-width-aligned) column
    // resolves a line the same way BreakPointMargin's own click handling does — only the Y
    // coordinate actually matters for finding the line; X is irrelevant here (unlike inside the
    // TextView itself, where X picks a column). Internal + InternalsVisibleTo, same pattern as
    // WorstSeverityOnLine, so this can be exercised directly with points that deliberately miss the
    // visual icon glyph, without needing to drive an actual PointerPressed/MenuFlyout interaction.
    internal int? ResolveLineFromPoint(Point point)
    {
        var offset = _editor.GetOffsetFromPoint(point);
        return offset == -1 ? null : TextView.Document.GetLineByOffset(offset).LineNumber;
    }

    // The candidate-resolution half of OnPointerPressed, factored out so a test can drive it
    // directly with a Point instead of needing to synthesize a real PointerPressedEventArgs
    // (Avalonia's pointer/device plumbing has no simple headless test surface for that) — same
    // "internal test hook" pattern as WorstSeverityOnLine/ResolveLineFromPoint right above. Only the
    // caret's own line ever has a visible icon (Render's own doc comment) — a click at some OTHER
    // line that merely also happens to have a fix returns empty rather than that line's candidates,
    // so a menu can never open with no icon having prompted it.
    internal IReadOnlyList<CodeFixEntry> ResolveCandidatesForClick(Point point)
    {
        var lineNumber = ResolveLineFromPoint(point);
        if (lineNumber is null || lineNumber != _caretLine) return [];
        return _fixes.Where(f => f.Line == lineNumber).ToList();
    }

    private void Apply(CodeFixEntry fix) => FixApplyRequested?.Invoke(fix);

    // Matches BreakPointMargin's own line-number-column width (via the shared
    // GutterMetrics helper) instead of an independent TextView.DefaultLineHeight * 0.8 — the two
    // margins sit right next to each other in the gutter, so a mismatched width made the lightbulb
    // and the line-number/debug-arrow icons look like two misaligned columns instead of one gutter.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (TextView is null) return new Size(0, 0);
        if (TextView.Document is null) return new Size(TextView.DefaultLineHeight, 0);
        return new Size(GutterMetrics.LineNumberColumnWidth(_editor, TextView, GutterHorizontalPadding), 0);
    }

    // Same padding constant as BreakPointMargin.HorizontalPadding — kept as a separate private
    // constant (rather than a shared public one) since the two margins have no other reason to
    // reference each other's internals; GutterMetrics is the only intentional coupling point.
    private const double GutterHorizontalPadding = 6.0;

    // Scale used to be Bounds.Width * 0.72 / 24.0 — fine back when Bounds.Width was
    // a small fixed constant, but this margin's width was later made to track BreakPointMargin's own
    // line-number-driven column (via GutterMetrics) so the two align. Left as Bounds.Width-scaled,
    // that made the icon itself balloon along with the column on any document needing more
    // line-number digits — the column's WIDTH needs to match its neighbor, but the icon's SIZE never
    // needed to grow with it. Scaling off TextView.DefaultLineHeight instead ties the icon to how
    // tall a line of text is, which is what it's actually centered against, not to the gutter's
    // width. Centering (cx) still reads Bounds.Width — the icon must center within whatever the
    // column's real width is, only its own size shouldn't depend on that width.
    //
    // The first size fix (0.6) shrank the glyph back down close to its original,
    // pre-alignment size — but that original size was itself always on the small side, and empirically
    // (ResolveLineFromPoint, verified with ProbeClickTests during investigation) a click ANYWHERE
    // across the margin's full row already resolves to the right line regardless of X — the click
    // target was never actually limited to the glyph's own pixels. So the "hard to hit with the
    // pointer" complaint was a visual-affordance problem, not a hit-testing bug: a small glyph
    // doesn't visually read as "aim here", even though the real clickable area is generous. Bumped
    // back up to a size close to the line height itself (comfortably visible/aimable, matching how
    // large VS/Rider's own indicator-margin glyphs read) without returning to the unbounded,
    // column-width-scaled size the gutter alignment accidentally produced.
    private const double IconScaleFactor = 0.85;

    internal (double Scale, double X, double Y) ComputeIconTransform(double lineH, double yTop)
    {
        var s = TextView.DefaultLineHeight * IconScaleFactor / 24.0;
        var cx = Bounds.Width / 2 - 12 * s;
        var cy = yTop + lineH / 2 - 12 * s;
        return (s, cx, cy);
    }

    private void DrawCentered(DrawingContext ctx, StreamGeometry geo, IBrush? brush, double yTop, double lineH)
    {
        if (brush is null) return;
        var (s, cx, cy) = ComputeIconTransform(lineH, yTop);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy)))
            ctx.DrawGeometry(brush, null, geo);
    }

    private static T? LoadResource<T>(string key) where T : class
    {
        object? res = null;
        Application.Current?.TryGetResource(key, null, out res);
        return res as T;
    }

    private static T? LoadThemedResource<T>(string key) where T : class
    {
        object? res = null;
        var variant = Application.Current?.ActualThemeVariant;
        Application.Current?.TryGetResource(key, variant, out res);
        return res as T;
    }
}
