using Avalonia;
using Avalonia.Controls;

namespace RunPad.UserControls;

public partial class DebugPanel : UserControl
{
    public DebugPanel()
    {
        InitializeComponent();
        var sections = this.FindControl<Grid>("StackSections")!;
        var threads = this.FindControl<Expander>("ThreadsSection")!;
        var stack = this.FindControl<Expander>("CallStackSection")!;
        var splitter = this.FindControl<GridSplitter>("ThreadsStackSplitter")!;
        GridLength threadSize = new(1, GridUnitType.Star);
        GridLength stackSize = new(2, GridUnitType.Star);
        void Reflow(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Expander.IsExpandedProperty) return;
            if (sections.RowDefinitions[0].Height.IsStar)
                threadSize = sections.RowDefinitions[0].Height;
            if (sections.RowDefinitions[2].Height.IsStar)
                stackSize = sections.RowDefinitions[2].Height;
            sections.RowDefinitions[0].Height = threads.IsExpanded ? threadSize : GridLength.Auto;
            sections.RowDefinitions[2].Height = stack.IsExpanded ? stackSize : GridLength.Auto;
            splitter.IsVisible = threads.IsExpanded && stack.IsExpanded;
            sections.RowDefinitions[1].Height = splitter.IsVisible ? new GridLength(1) : new GridLength(0);
        }
        threads.PropertyChanged += Reflow;
        stack.PropertyChanged += Reflow;
    }
}
