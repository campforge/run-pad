using Avalonia;
using Avalonia.Controls;

namespace Invoklet.UserControls;

/// <summary>
/// Actions for a sidebar section (an <c>Expander.bundle</c>, Resources/NavigationStyles.axaml),
/// shown at its header's right edge, past the open/close marker: a <see cref="TreeActionButton"/>,
/// or a panel of them.
/// </summary>
public static class SidebarSection
{
    public static readonly AttachedProperty<object?> ActionsProperty =
        AvaloniaProperty.RegisterAttached<Expander, object?>("Actions", typeof(SidebarSection));

    public static object? GetActions(Expander section) => section.GetValue(ActionsProperty);

    public static void SetActions(Expander section, object? value) => section.SetValue(ActionsProperty, value);
}
