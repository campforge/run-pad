using Avalonia.Interactivity;
using RunPad.Services;

namespace RunPad.Windows;

/// <summary>Closing the app with unsaved tabs while draft recovery is on: Save All, Keep in Stash or Cancel.</summary>
public partial class ExitUnsavedDialog : RunPadDialogWindow
{
    // Enough names to recognize the tabs without the dialog growing with every open tab.
    private const int ListedNames = 5;

    public ExitUnsavedDialog(IReadOnlyList<string> fileNames)
        : this()
    {
        var listed = string.Join(", ", fileNames.Take(ListedNames));
        if (fileNames.Count > ListedNames) listed += $" and {fileNames.Count - ListedNames} more";
        Layout.Message = (fileNames.Count == 1 ? "1 tab has unsaved changes: " : $"{fileNames.Count} tabs have unsaved changes: ")
            + listed + ". Keep in Stash closes without writing them; they reopen next time.";
    }

    private ExitUnsavedDialog()
    {
        InitializeComponent();
        // Three labelled actions; wider than the two-answer dialogs so they never crowd the edge.
        Width = 520;
    }

    private void SaveAllButton_Click(object? sender, RoutedEventArgs e)
        => Close((ExitUnsavedChoice?)ExitUnsavedChoice.SaveAll);

    private void KeepInStashButton_Click(object? sender, RoutedEventArgs e)
        => Close((ExitUnsavedChoice?)ExitUnsavedChoice.KeepInStash);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close((ExitUnsavedChoice?)ExitUnsavedChoice.Cancel);
}
