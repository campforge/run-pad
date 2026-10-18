using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunPad.Windows;

/// <summary>
/// Asks before a tab's unsaved edits give way to its file on disk: when another program changed
/// the file, and for File → Revert to Saved. Closes with true to reload, false to keep the edits.
/// </summary>
public partial class ReloadFileDialog : Window
{
    public static ReloadFileDialog ChangedOnDisk(string fileName) => new(
        "File Changed on Disk",
        $"\"{fileName}\" was changed by another program, and you have unsaved changes to it. " +
        "Reload the file and lose your changes, or keep your version? Saving it later asks before replacing the file.",
        "Reload", "Keep My Changes");

    public static ReloadFileDialog Revert(string fileName) => new(
        "Revert to Saved",
        $"Revert \"{fileName}\" to the version saved on disk? Your unsaved changes will be lost.",
        "Revert", "Cancel");

    private ReloadFileDialog(string title, string message, string reload, string keep)
        : this()
    {
        Title = title;
        MessageBlock.Text = message;
        ReloadButton.Content = reload;
        KeepButton.Content = keep;
    }

    private ReloadFileDialog() => InitializeComponent();

    private void ReloadButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void KeepButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
