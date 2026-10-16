using Avalonia.Controls;
using Avalonia.VisualTree;

namespace RunPad.UserControls;

public partial class DebugPanel : UserControl
{
    public DebugPanel()
    {
        InitializeComponent();
        var tabs = this.FindControl<TabControl>("VariableTabs")!;
        tabs.SizeChanged += (_, e) =>
        {
            // Keep settings at the trailing edge; preserve space for both view selectors.
            var button = tabs.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ExceptionsButton");
            if (button is not null) button.Content = e.NewSize.Width < 280 ? "⋯" : "Exceptions…";
        };
    }
}
