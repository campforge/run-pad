using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Invoklet.TemplatedControls;

// An action menu rather than an empty selection field: choosing a name inserts a placeholder.
public sealed class SecretPicker : DropDownButton
{
    public static readonly StyledProperty<IReadOnlyList<string>?> SecretNamesProperty =
        AvaloniaProperty.Register<SecretPicker, IReadOnlyList<string>?>(nameof(SecretNames));

    public static readonly StyledProperty<string?> SelectedSecretProperty =
        AvaloniaProperty.Register<SecretPicker, string?>(nameof(SelectedSecret), defaultBindingMode: BindingMode.TwoWay);

    protected override Type StyleKeyOverride => typeof(DropDownButton);

    public IReadOnlyList<string>? SecretNames
    {
        get => GetValue(SecretNamesProperty);
        set => SetValue(SecretNamesProperty, value);
    }

    public string? SelectedSecret
    {
        get => GetValue(SelectedSecretProperty);
        set => SetValue(SelectedSecretProperty, value);
    }

    public SecretPicker()
    {
        Content = "Insert secret";
        Classes.Add("secondary");
        MinWidth = 116;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SecretNamesProperty) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var name in SecretNames ?? [])
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) =>
            {
                SetCurrentValue(SelectedSecretProperty, name);
                SetCurrentValue(SelectedSecretProperty, null);
            };
            menu.Items.Add(item);
        }
        Flyout = menu;
    }
}
