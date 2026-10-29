using Avalonia.Controls;
using Avalonia.Interactivity;
using Invoklet.Format.MdRun;

namespace Invoklet.Windows;

public partial class NewFileKindDialog : Window
{
    private static readonly MdRunKind[] Kinds = [MdRunKind.Statements, MdRunKind.Program, MdRunKind.Expression];

    public NewFileKindDialog()
    {
        InitializeComponent();
        KindBox.ItemsSource = Kinds;
        KindBox.SelectedIndex = 0;
        Opened += (_, _) => KindBox.Focus();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(KindBox.SelectedItem as MdRunKind?);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
