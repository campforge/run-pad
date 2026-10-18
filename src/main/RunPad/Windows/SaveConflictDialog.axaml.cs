using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Services;

namespace RunPad.Windows;

public partial class SaveConflictDialog : Window
{
    public SaveConflictDialog(string fileName)
        : this()
    {
        MessageBlock.Text =
            $"\"{fileName}\" was changed by another program after RunPad opened or last saved it. " +
            "Save your version under another name, overwrite the file on disk with it, or cancel.";
    }

    private SaveConflictDialog() => InitializeComponent();

    private void SaveAsButton_Click(object? sender, RoutedEventArgs e)
        => Close((SaveConflictChoice?)SaveConflictChoice.SaveAs);

    private void OverwriteButton_Click(object? sender, RoutedEventArgs e)
        => Close((SaveConflictChoice?)SaveConflictChoice.Overwrite);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close((SaveConflictChoice?)SaveConflictChoice.Cancel);
}
