using Avalonia.Controls;
using DotPad.ViewModels;

namespace DotPad.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}