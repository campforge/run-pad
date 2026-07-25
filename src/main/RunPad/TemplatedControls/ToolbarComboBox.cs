using Avalonia.Controls;

namespace RunPad.TemplatedControls;

public class ToolbarComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);
}
