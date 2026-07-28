using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunPad.Windows;

public partial class ErrorDialog : Window
{
    public ErrorDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageBlock.Text = message;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();
}
