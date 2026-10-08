using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Services;

namespace RunPad.Windows;

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog(string fileName)
        : this()
    {
        MessageBlock.Text = $"\"{fileName}\" has unsaved changes. Save before closing?";
    }

    private UnsavedChangesDialog() => InitializeComponent();

    // The same Save / Don't Save / Cancel choice for something that isn't a file (the Dependencies draft).
    public static UnsavedChangesDialog WithMessage(string message)
    {
        var dialog = new UnsavedChangesDialog();
        dialog.MessageBlock.Text = message;
        return dialog;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.Save);

    private void DontSaveButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.DontSave);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.Cancel);
}
