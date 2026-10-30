using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;

namespace Invoklet.Avalonia.CodeTextEditor.Margins;

// The thin line between the gutter and the code: one logical pixel at the code side of the gutter,
// in the theme's separator color, the full height of the editor (so it stays continuous while
// scrolling). It takes no clicks — they reach the text area as before.
internal sealed class GutterSeparatorMargin : AbstractMargin
{
    internal const double Thickness = 1.0;

    public GutterSeparatorMargin()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Thickness, 0);

    public override void Render(DrawingContext context)
    {
        if (GutterMetrics.ThemedResource<IBrush>("Invoklet.Separator.BackgroundBrush") is { } brush)
            context.FillRectangle(brush, new Rect(0, 0, Thickness, Bounds.Height));
    }
}
