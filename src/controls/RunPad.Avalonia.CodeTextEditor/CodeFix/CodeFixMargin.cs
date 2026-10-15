using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public CodeFixMargin(CodeTextEditor editor)
    {
        _editor = editor;
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
        // Avalonia hit-tests a control by what it draws. With only the glyph drawn, a click on the
        // square's padding (or anywhere else in the column) went past this margin to the text area
        // and moved the caret. A transparent fill paints nothing visible but makes the whole column
        // this margin's; ResolveCandidatesForClick decides which part of it opens the menu.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        GutterMetrics.DrawCurrentLineRow(context, TextView, _caretLine, Bounds.Width);

        if (BulbLayout() is not var (severity, yTop, rowHeight)) return;

        _bulbGeo ??= LoadResource<StreamGeometry>("Icon.Lightbulb");
        if (_bulbGeo is null) return;

        // Only the glyph: the square around it is an invisible click target (HitTarget), with no
        // frame or background, not even on hover.
        var brush = LoadThemedResource<IBrush>(SeverityBrushKey(severity));
        DrawCentered(context, _bulbGeo, brush, yTop, rowHeight);
    }

    // Where the bulb is: the caret's line, when it has a fix and is on screen — the worst severity
    // there, and the top and height of the line's first rendered row in this margin's coordinates.
    // Shared by Render and the click target, so a click hits exactly what is drawn.
    private (CodeFixSeverity Severity, double YTop, double RowHeight)? BulbLayout()
    {
        if (TextView is null || !TextView.VisualLinesValid || TextView.VisualLines.Count == 0 || _fixes.Count == 0)
            return null;

        if (WorstSeverityOnLine(_caretLine) is not { } worst) return null;

        var vl = TextView.VisualLines.FirstOrDefault(v => v.FirstDocumentLine.LineNumber == _caretLine);
        if (vl is null) return null; // caret line currently scrolled out of view — nothing to draw

        // The first rendered row of the caret's line: on a wrapped line the bulb belongs next to the
        // row where the line starts (with its line number), not in the middle of the whole block.
        var (yTop, rowHeight) = GutterMetrics.FirstRow(TextView, vl);
        return (worst, yTop, rowHeight);
    }

    // Test-only (InternalsVisibleTo): where the glyph's own shape is drawn now (the geometry's bounds
    // under the drawing transform), or null when there is no bulb or no glyph resource.
    internal Rect? GlyphBounds()
    {
        if (BulbLayout() is not var (_, yTop, rowHeight)) return null;
        _bulbGeo ??= LoadResource<StreamGeometry>("Icon.Lightbulb");
        if (_bulbGeo is null) return null;
        var (s, x, y) = ComputeIconTransform(rowHeight, yTop);
        return _bulbGeo.Bounds.TransformToAABB(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y));
    }

    // The bulb's click target: a square centered on the glyph, larger than it — 24 logical pixels, or
    // the glyph plus a margin when a large font makes the glyph itself bigger — but never wider than
    // this margin's own column, so it can't overlap the line numbers or the folding margin. It may
    // reach a little above and below a short line, into this column only, where nothing else is.
    internal const double MinimumHitTargetSize = 24;

    internal Rect? HitTarget()
    {
        if (BulbLayout() is not var (_, yTop, lineH)) return null;

        var side = Math.Min(Bounds.Width, HitTargetSide());
        var center = new Point(Bounds.Width / 2, yTop + lineH / 2);
        return new Rect(center.X - side / 2, center.Y - side / 2, side, side);
    }

    // A hand only over the bulb's target: elsewhere a click does nothing.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = HitTarget() is { } target && target.Contains(e.GetPosition(this)) ? HandCursor : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = Cursor.Default;
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

    // Clicks in the column are this margin's (its transparent fill, see Render): none reaches the text area. A press
    // inside the bulb's square is remembered and the menu opens when the button is released inside it
    // too — a click, like a button or a context menu, rather than on the press itself, so the opening
    // popup is not in the way of the rest of the press (pointer capture, focus, the release).
    private bool _pressedOnBulb;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        _pressedOnBulb = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                         && ResolveCandidatesForClick(e.GetPosition(this)).Count > 0;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Handled = true;
        if (!_pressedOnBulb || e.InitialPressMouseButton != MouseButton.Left) return;
        _pressedOnBulb = false;

        var candidates = ResolveCandidatesForClick(e.GetPosition(this));
        if (candidates.Count == 0) return; // released outside the square
        ShowFixMenu(candidates);
    }

    // The menu for the caret line's fixes, below the bulb's square.
    internal void ShowFixMenu(IReadOnlyList<CodeFixEntry> candidates)
    {
        if (HitTarget() is not { } target) return;

        // Always go through the menu, even for a single candidate — VS/Rider-style, so the lightbulb
        // never applies anything sight-unseen and there's a consistent place to add more entries later
        // (Suppress, Fix all in file/project, ...) without a special case for "exactly one fix".
        var menu = new MenuFlyout();
        foreach (var item in BuildMenuItems(candidates))
            menu.Items.Add(item);
        // Anchored to the bulb's square, just below it. Not to the margin control (it spans the whole
        // editor height: the menu ended up at its bottom), and not "at the pointer": that placement
        // uses the window's last known pointer position, and when there is none the menu opens at the
        // top-left corner of the screen, away from the window.
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Popup.PlacementRect = target;
        menu.ShowAt(this);
        OpenMenu = menu;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressedOnBulb = false;
    }

    // Test-only (InternalsVisibleTo): the menu the last click opened.
    internal MenuFlyout? OpenMenu { get; private set; }

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
    // caret's own line ever has a visible icon (Render's own doc comment), and only a click inside its
    // square HitTarget counts — anywhere in it, padding and corners included, does what a click on the
    // glyph does, even where the square reaches past a short line; outside it, nothing happens.
    internal IReadOnlyList<CodeFixEntry> ResolveCandidatesForClick(Point point)
    {
        if (HitTarget() is not { } target || !target.Contains(point)) return [];
        return _fixes.Where(f => f.Line == _caretLine).ToList();
    }

    private void Apply(CodeFixEntry fix) => FixApplyRequested?.Invoke(fix);

    // The column is exactly as wide as the bulb's square: a compact gutter, with the glyph right
    // next to the line numbers (as in Rider), and one formula for both the width and the click
    // target, so they cannot drift apart.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (TextView is null) return new Size(0, 0);
        return new Size(ColumnWidth(), 0);
    }

    private double ColumnWidth() => HitTargetSide();

    // 24 logical pixels, or the glyph plus a margin when a large font makes the glyph bigger.
    private double HitTargetSide() => Math.Max(MinimumHitTargetSize, TextView.DefaultLineHeight * IconScaleFactor + 8);

    // A new font size (zoom) or line count changes the column's width, and the bulb and its square
    // must stay inside it. Re-measured after the text view's own layout pass, not during it — same
    // deferral as BreakPointMargin's width check.
    private bool _measureInvalidationPending;

    protected override void OnTextViewVisualLinesChanged()
    {
        base.OnTextViewVisualLinesChanged();
        if (TextView is null || _measureInvalidationPending || Math.Abs(ColumnWidth() - Bounds.Width) <= 0.5) return;
        _measureInvalidationPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _measureInvalidationPending = false;
            InvalidateMeasure();
        });
    }


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
