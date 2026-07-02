using Avalonia.Controls;
using Avalonia.Interactivity;
using DotPad.UserControls.Models;

namespace DotPad.UserControls;

public partial class DebugPanel : UserControl
{
    public DebugPanel()
    {
        InitializeComponent();
    }

    private void AddWatch_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditorTabItemViewModel vm)
            vm.WatchExpressions.Add(new WatchExpression());
    }

    private void RemoveWatch_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditorTabItemViewModel vm && WatchGrid.SelectedItem is WatchExpression expr)
            vm.WatchExpressions.Remove(expr);
    }
}
