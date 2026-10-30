using Avalonia.Controls;
using Invoklet.Windows.Models;

namespace Invoklet.Windows;

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
