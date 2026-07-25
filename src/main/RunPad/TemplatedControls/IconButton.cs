using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RunPad.TemplatedControls;

public class IconButton : Button
{
    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<IconButton, Geometry?>(nameof(IconData));

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }
}
