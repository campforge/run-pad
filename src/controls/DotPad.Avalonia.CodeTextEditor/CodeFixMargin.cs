using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace DotPad.Avalonia.CodeTextEditor;

// Lightbulb gutter margin: shows an icon on lines with an available quick fix and applies it on
// click. Real Roslyn CodeFixProviders (RoslynCodeFixEngine) routinely offer several candidates for
// one line (e.g. multiple "using X;" namespaces for the same unresolved type) — a single click
// applies the only fix directly, or opens a small picker menu when there's more than one.
public class CodeFixMargin : AbstractMargin
{
    static CodeFixMargin()
    {
        FocusableProperty.OverrideDefaultValue(typeof(CodeFixMargin), false);
    }

    private readonly CodeTextEditor _editor;
    private IReadOnlyList<CodeFixEntry> _fixes = [];
    private StreamGeometry? _bulbGeo;

    // Raised when an applied fix carries namespaces to import — this control has no notion of a
    // "project file"/front matter to route them to (see CodeFixEntry's doc comment), so it's left
    // to whoever owns that (forwarded up via CodeTextEditor.UsingsToAddRequested).
    public event Action<IReadOnlyList<string>>? UsingsToAddRequested;

    public CodeFixMargin(CodeTextEditor editor)
    {
        _editor = editor;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public void Update(IReadOnlyList<CodeFixEntry> fixes)
    {
        _fixes = fixes;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!TextView.VisualLinesValid || TextView.VisualLines.Count == 0 || _fixes.Count == 0) return;

        _bulbGeo ??= LoadResource<StreamGeometry>("Icon.Lightbulb");
        if (_bulbGeo is null) return;

        var brush = LoadThemedResource<IBrush>("DotPad.Margin.DebugArrowBrush");

        foreach (var vl in TextView.VisualLines)
        {
            var lineNo = vl.FirstDocumentLine.LineNumber;
            if (!_fixes.Any(f => f.Line == lineNo)) continue;

            var lineH = vl.Height;
            var yTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop)
                          - TextView.VerticalOffset;
            DrawCentered(context, _bulbGeo, brush, yTop, lineH);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));
        if (offset == -1) return;

        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;
        var candidates = _fixes.Where(f => f.Line == lineNumber).ToList();
        if (candidates.Count == 0) return;

        // Always go through the menu, even for a single candidate — VS/Rider-style, so the lightbulb
        // never applies anything sight-unseen and there's a consistent place to add more entries later
        // (Suppress, Fix all in file/project, ...) without a special case for "exactly one fix".
        var menu = new MenuFlyout();
        foreach (var fix in candidates)
        {
            var item = new MenuItem { Header = fix.Title };
            item.Click += (_, _) => Apply(fix);
            menu.Items.Add(item);
        }
        // showAtPointer: true — anchors the flyout to where the user actually clicked (the lightbulb
        // icon on this specific line) instead of PopupFlyoutBase's default placement relative to the
        // whole margin control, which spans the full editor height and made the menu appear pinned to
        // the bottom-left corner regardless of which line's lightbulb was clicked.
        menu.ShowAt(this, true);

        e.Handled = true;
    }

    private void Apply(CodeFixEntry fix)
    {
        if (_editor.Document.Text != fix.NewCode)
            _editor.Document.Text = fix.NewCode;
        if (fix.UsingsToAdd.Count > 0)
            UsingsToAddRequested?.Invoke(fix.UsingsToAdd);
    }

    protected override Size MeasureOverride(Size availableSize)
        => TextView != null ? new Size(TextView.DefaultLineHeight * 0.8, 0) : new Size(0, 0);

    private void DrawCentered(DrawingContext ctx, StreamGeometry geo, IBrush? brush, double yTop, double lineH)
    {
        if (brush is null) return;
        var s = Bounds.Width * 0.72 / 24.0;
        var cx = Bounds.Width / 2 - 12 * s;
        var cy = yTop + lineH / 2 - 12 * s;
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
