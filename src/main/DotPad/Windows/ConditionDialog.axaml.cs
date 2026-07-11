using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotPad.Windows;

public partial class ConditionDialog : Window
{
    public ConditionDialog(string? initialCondition = null, string? initialHitCondition = null)
    {
        InitializeComponent();
        ConditionBox.Text = initialCondition ?? "";
        HitConditionBox.Text = initialHitCondition ?? "";
        Opened += (_, _) =>
        {
            ConditionBox.Focus();
            ConditionBox.SelectAll();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(((string?, string?)?)(ConditionBox.Text ?? "", HitConditionBox.Text ?? ""));

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
