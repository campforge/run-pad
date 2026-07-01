using Avalonia.Controls;
using DotPad.Windows.Models;

namespace DotPad.Windows;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}