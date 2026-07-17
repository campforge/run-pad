using Avalonia.Controls;
using Avalonia.Interactivity;
using DotPad.Services;

namespace DotPad.Windows;

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog(string fileName)
    {
        InitializeComponent();
        MessageBlock.Text = $"\"{fileName}\" has unsaved changes. Save before closing?";
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.Save);

    private void DontSaveButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.DontSave);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close((UnsavedChangesResult?)UnsavedChangesResult.Cancel);
}
