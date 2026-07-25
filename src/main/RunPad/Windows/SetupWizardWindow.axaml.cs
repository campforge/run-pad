using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Windows.Models;

namespace RunPad.Windows;

public partial class SetupWizardWindow : Window
{
    public event Action? Continued;

    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public SetupWizardWindow() => InitializeComponent();

    private void Continue_Click(object? sender, RoutedEventArgs e) => Continued?.Invoke();
}
