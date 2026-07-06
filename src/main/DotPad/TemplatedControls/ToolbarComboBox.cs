using Avalonia.Controls;

namespace DotPad.TemplatedControls;

public class ToolbarComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);
}
