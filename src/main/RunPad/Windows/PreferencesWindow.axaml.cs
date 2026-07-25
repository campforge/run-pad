using Avalonia.Controls;
using RunPad.Windows.Models;

namespace RunPad.Windows;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    public PreferencesWindow() => InitializeComponent();
}
