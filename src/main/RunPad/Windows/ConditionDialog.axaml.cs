using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotPad.Windows;

public partial class ConditionDialog : Window
{
    public ConditionDialog(string? initialCondition = null, string? initialHitCondition = null, bool initialEnabled = true)
    {
        InitializeComponent();
        EnabledBox.IsChecked = initialEnabled;
        ConditionBox.Text = initialCondition ?? "";
        HitConditionBox.Text = initialHitCondition ?? "";
        Opened += (_, _) =>
        {
            ConditionBox.Focus();
            ConditionBox.SelectAll();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(((string?, string?, bool)?)(ConditionBox.Text ?? "", HitConditionBox.Text ?? "", EnabledBox.IsChecked ?? true));

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
