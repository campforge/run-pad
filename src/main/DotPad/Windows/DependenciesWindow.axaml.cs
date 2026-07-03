using Avalonia.Controls;
using Avalonia.Interactivity;
using DotPad.Windows.Models;

namespace DotPad.Windows;

public partial class DependenciesWindow : Window
{
    public DependenciesWindow(DependenciesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
