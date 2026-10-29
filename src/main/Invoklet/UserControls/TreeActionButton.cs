using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Invoklet.UserControls;

/// <summary>
/// A small icon-only button with a thin border, for actions that sit in a sidebar tree's section
/// header or rows (the Connections header's Add, for instance). Its look lives in
/// Resources/NavigationStyles.axaml. Set <see cref="Icon"/>, a tooltip and an accessible name: the
/// icon alone carries no text.
/// </summary>
public class TreeActionButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<TreeActionButton, Geometry?>(nameof(Icon));

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
