using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Windows.Models;

namespace RunPad.Windows;

// Closes with true after the store was saved, false/null otherwise.
public partial class SecretStoreWindow : Window
{
    public SecretStoreWindow(SecretStoreWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public SecretStoreWindow() => InitializeComponent();

    private SecretStoreWindowViewModel? ViewModel => DataContext as SecretStoreWindowViewModel;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
