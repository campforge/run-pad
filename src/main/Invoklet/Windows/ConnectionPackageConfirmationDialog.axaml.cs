using Avalonia.Interactivity;

namespace Invoklet.Windows;

public partial class ConnectionPackageConfirmationDialog : InvokletDialogWindow
{
    public ConnectionPackageConfirmationDialog(string message)
    {
        InitializeComponent();
        Layout.Message = message;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Add_Click(object? sender, RoutedEventArgs e) => Close(true);
}
