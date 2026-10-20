using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Windows.Models;

namespace RunPad.Windows;

// Closes with true after the list was saved, false/null otherwise.
public partial class ConnectionsWindow : Window
{
    public ConnectionsWindow(ConnectionsWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // One Add entry per driver this RunPad has, in registration order.
        var menu = (MenuFlyout)AddButton.Flyout!;
        foreach (var driver in viewModel.Drivers)
            menu.Items.Add(new MenuItem { Header = driver.DisplayName, Command = viewModel.AddCommand, CommandParameter = driver });
    }

    public ConnectionsWindow() => InitializeComponent();

    private ConnectionsWindowViewModel? ViewModel => DataContext as ConnectionsWindowViewModel;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
