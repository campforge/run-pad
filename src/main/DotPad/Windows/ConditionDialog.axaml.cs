using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotPad.Windows;

public partial class ConditionDialog : Window
{
    public ConditionDialog(string? initialCondition = null)
    {
        InitializeComponent();
        ConditionBox.Text = initialCondition ?? "";
        Opened += (_, _) =>
        {
            ConditionBox.Focus();
            ConditionBox.SelectAll();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(ConditionBox.Text ?? "");

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
