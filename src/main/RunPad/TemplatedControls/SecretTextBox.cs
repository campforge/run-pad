using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace RunPad.TemplatedControls;

// A masked TextBox also needs to hide its automation value and value-change events.
// The standard TextBox peer exposes Text even when PasswordChar is set.
public sealed class SecretTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    protected override AutomationPeer OnCreateAutomationPeer() => new SecretPeer(this);

    private sealed class SecretPeer(SecretTextBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        string? IValueProvider.Value => Owner.PasswordChar == '\0' ? Owner.Text : "";

        void IValueProvider.SetValue(string? value)
        {
            if (!Owner.IsEffectivelyEnabled || Owner.IsReadOnly)
                throw new InvalidOperationException("The secret field is not editable.");
            Owner.SetCurrentValue(TextProperty, value);
        }

        // The field's value is exposed through IValueProvider, never through template children.
        protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;

        protected override void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextProperty && Owner.PasswordChar == '\0')
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, e.OldValue, e.NewValue);
            else if (e.Property == PasswordCharProperty)
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty,
                    null, e.NewValue is '\0' ? Owner.Text : "");
        }
    }
}
