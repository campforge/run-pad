using Avalonia.Interactivity;

namespace RunPad.Windows;

public partial class ConnectionPackageConfirmationDialog : RunPadDialogWindow
{
    public ConnectionPackageConfirmationDialog(string message)
    {
        InitializeComponent();
        Layout.Message = message;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Add_Click(object? sender, RoutedEventArgs e) => Close(true);
}
