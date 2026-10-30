using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Invoklet.Windows;

public partial class ErrorDialog : Window
{
    public ErrorDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleBlock.Text = title;
        MessageBlock.Text = message;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();
}
