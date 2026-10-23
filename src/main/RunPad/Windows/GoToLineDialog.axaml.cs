using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunPad.Windows;

public partial class GoToLineDialog : Window
{
    public GoToLineDialog(int currentLine, int maxLine)
    {
        InitializeComponent();
        BorderlessWindowChrome.Attach(this, DialogSurface);
        PromptText.Text = $"Go to line (1-{maxLine}):";
        LineBox.Maximum = maxLine;
        LineBox.Value = Math.Clamp(currentLine, 1, maxLine);
        Opened += (_, _) =>
        {
            LineBox.Focus();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close((int?)LineBox.Value);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}
