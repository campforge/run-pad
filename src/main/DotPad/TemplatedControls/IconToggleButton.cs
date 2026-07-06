using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace DotPad.TemplatedControls;

public class IconToggleButton : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<IconButton, Geometry?>(nameof(IconData));

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }
}
