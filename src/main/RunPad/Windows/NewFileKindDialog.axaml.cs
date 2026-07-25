using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Format.RPad;

namespace RunPad.Windows;

public partial class NewFileKindDialog : Window
{
    private static readonly RpadKind[] Kinds = [RpadKind.Statements, RpadKind.Program, RpadKind.Expression];

    public NewFileKindDialog()
    {
        InitializeComponent();
        KindBox.ItemsSource = Kinds;
        KindBox.SelectedIndex = 0;
        Opened += (_, _) => KindBox.Focus();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(KindBox.SelectedItem as RpadKind?);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
