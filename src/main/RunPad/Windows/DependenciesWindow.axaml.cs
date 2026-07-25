using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RunPad.Format.RPad;
using RunPad.Windows.Models;

namespace RunPad.Windows;

public partial class DependenciesWindow : Window
{
    public DependenciesWindow(DependenciesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    public DependenciesWindow() => InitializeComponent();

    public void SetFrontMatter(RpadFrontMatter? fm)
    {
        if (DataContext is DependenciesWindowViewModel vm)
            vm.SetFrontMatter(fm);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void NewUsing_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is DependenciesWindowViewModel vm)
            _ = vm.AddUsingCommand.Execute(Unit.Default).Subscribe(_ => { }, _ => { });
    }
}
