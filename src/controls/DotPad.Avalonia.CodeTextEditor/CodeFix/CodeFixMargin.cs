using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace DotPad.Avalonia.CodeTextEditor;

// Lightbulb gutter margin: shows an icon on the CURRENT (caret) line when it has an available quick
// fix, and applies it on click. Real Roslyn CodeFixProviders (RoslynCodeFixEngine) routinely offer
// several candidates for one line (e.g. multiple "using X;" namespaces for the same unresolved
// type) — a single click applies the only fix directly, or opens a small picker menu when there's
// more than one.
//
// User request 2026-07-21 (TODO.md #74): only the caret's own line ever shows the icon, VS/Rider-
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
    private StreamGeometry? _hammerGeo;

    // Tracks the caret's own document line so PositionChanged (fired on every column move too, not
    // just line changes — e.g. Left/Right arrow within the same line) only triggers a repaint when
    // the line the icon could be drawn on actually changed, not on every single caret move.
    private int _caretLine;

    // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход) — raised when the user
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

    // TODO.md #75: several fixes can share one line (e.g. multiple "using X;" candidates for the
    // same unresolved type) at different severities — the icon shows whichever is worse, same
    // priority order BreakPointMargin's own debug-arrow/breakpoint overlap uses (an error is more
    // worth the user's attention than a warning sitting on the same line). Internal (not private) +
    // InternalsVisibleTo (see the csproj), same pattern as CSharpReferenceService.IsManagedAssembly
    // (TODO.md #44) — so this and SeverityBrushKey can be tested directly without a live TextView.
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
        CodeFixSeverity.Error => "DotPad.Margin.CodeFixErrorBrush",
        CodeFixSeverity.Warning => "DotPad.Margin.CodeFixWarningBrush",
        _ => "DotPad.Margin.DebugArrowBrush",
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var candidates = ResolveCandidatesForClick(e.GetPosition(this));
        if (candidates.Count == 0) return;

        // Always go through the menu, even for a single candidate — VS/Rider-style, so the lightbulb
        // never applies anything sight-unseen and there's a consistent place to add more entries later
        // (Suppress, Fix all in file/project, ...) without a special case for "exactly one fix".
        var menu = new MenuFlyout();
        foreach (var fix in candidates)
            menu.Items.Add(BuildMenuItem(fix));
        // showAtPointer: true — anchors the flyout to where the user actually clicked (the lightbulb
        // icon on this specific line) instead of PopupFlyoutBase's default placement relative to the
        // whole margin control, which spans the full editor height and made the menu appear pinned to
        // the bottom-left corner regardless of which line's lightbulb was clicked.
        menu.ShowAt(this, true);

        e.Handled = true;
    }

    // TODO.md #79: same "PathIcon per MenuItem" pattern as FileTreeView.axaml's Rename/Delete menu
    // items — this menu is built in code-behind rather than XAML, so it's the same StreamGeometry
    // resource assigned via a fresh PathIcon instance per item (Avalonia visuals can't be shared
    // across multiple items) instead of a XAML <MenuItem.Icon>. One uniform hammer icon for every
    // entry is enough today — see TODO #79 for why per-action-kind icons aren't needed yet. Internal
    // (not private) + InternalsVisibleTo, same pattern as WorstSeverityOnLine, so the icon wiring can
    // be asserted directly without driving a real PointerPressed/MenuFlyout interaction.
    internal MenuItem BuildMenuItem(CodeFixEntry fix)
    {
        _hammerGeo ??= LoadResource<StreamGeometry>("Icon.Hammer");

        var item = new MenuItem { Header = fix.Title };
        if (_hammerGeo is not null)
            item.Icon = new PathIcon { Data = _hammerGeo, Width = 16, Height = 16 };
        item.Click += (_, _) => Apply(fix);
        return item;
    }

    // A click anywhere across the margin's full (now gutter-width-aligned, TODO.md #75) column
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

    // TODO.md #75: matches BreakPointMargin's own line-number-column width (via the shared
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

    // TODO.md #82/#84: scale used to be Bounds.Width * 0.72 / 24.0 — fine back when Bounds.Width was
    // a small fixed constant, but TODO #75 made this margin's width track BreakPointMargin's own
    // line-number-driven column (via GutterMetrics) so the two align. Left as Bounds.Width-scaled,
    // that made the icon itself balloon along with the column on any document needing more
    // line-number digits — the column's WIDTH needs to match its neighbor, but the icon's SIZE never
    // needed to grow with it. Scaling off TextView.DefaultLineHeight instead ties the icon to how
    // tall a line of text is, which is what it's actually centered against, not to the gutter's
    // width. Centering (cx) still reads Bounds.Width — the icon must center within whatever the
    // column's real width is, only its own size shouldn't depend on that width.
    //
    // TODO.md #84: #82's first pass (0.6) shrank the glyph back down close to its original,
    // pre-#75 size — but that original size was itself always on the small side, and empirically
    // (ResolveLineFromPoint, verified with ProbeClickTests during investigation) a click ANYWHERE
    // across the margin's full row already resolves to the right line regardless of X — the click
    // target was never actually limited to the glyph's own pixels. So the "hard to hit with the
    // pointer" complaint was a visual-affordance problem, not a hit-testing bug: a small glyph
    // doesn't visually read as "aim here", even though the real clickable area is generous. Bumped
    // back up to a size close to the line height itself (comfortably visible/aimable, matching how
    // large VS/Rider's own indicator-margin glyphs read) without returning to the unbounded,
    // column-width-scaled size #75 accidentally produced.
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
