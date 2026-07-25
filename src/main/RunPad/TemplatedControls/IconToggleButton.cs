using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotPad.TemplatedControls;

public class IconToggleButton : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<IconToggleButton, Geometry?>(nameof(IconData));

    /// <summary>Icon shown while checked; falls back to <see cref="IconData"/> when unset.</summary>
    public static readonly StyledProperty<Geometry?> CheckedIconDataProperty =
        AvaloniaProperty.Register<IconToggleButton, Geometry?>(nameof(CheckedIconData));

    /// <summary>Icon and border color while checked.</summary>
    public static readonly StyledProperty<IBrush?> CheckedBrushProperty =
        AvaloniaProperty.Register<IconToggleButton, IBrush?>(nameof(CheckedBrush));

    // Used by the checked-state template style to fall back to IconData when CheckedIconData
    // isn't set, so callers that don't need a distinct checked-state icon don't have to repeat it.
    public static readonly IMultiValueConverter PreferCheckedIconConverter =
        new FuncMultiValueConverter<Geometry?, Geometry?>(values => values.FirstOrDefault(v => v is not null));

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public Geometry? CheckedIconData
    {
        get => GetValue(CheckedIconDataProperty);
        set => SetValue(CheckedIconDataProperty, value);
    }

    public IBrush? CheckedBrush
    {
        get => GetValue(CheckedBrushProperty);
        set => SetValue(CheckedBrushProperty, value);
    }
}
