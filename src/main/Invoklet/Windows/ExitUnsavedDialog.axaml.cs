using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Invoklet.Services;

namespace Invoklet.Windows;

/// <summary>Closing the app with unsaved tabs while draft recovery is on: Save All, Keep in Stash or Cancel.</summary>
public partial class ExitUnsavedDialog : InvokletDialogWindow
{
    // Enough names to recognize the tabs without the dialog growing with every open tab.
    private const int ListedNames = 5;

    public ExitUnsavedDialog(IReadOnlyList<string> fileNames)
        : this()
    {
        Layout.Message = fileNames.Count == 1 ? "1 tab has unsaved changes." : $"{fileNames.Count} tabs have unsaved changes.";
        var nameRows = new StackPanel { Spacing = 4 };
        foreach (var name in fileNames.Take(ListedNames))
        {
            var row = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(row, name);
            nameRows.Children.Add(row);
        }
        if (fileNames.Count > ListedNames)
            nameRows.Children.Add(new TextBlock { Text = $"and {fileNames.Count - ListedNames} more" });

        var names = new StackPanel { Spacing = 4 };
        names.Children.Add(new ScrollViewer
        {
            Content = nameRows,
            MaxHeight = 128,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        names.Children.Add(new TextBlock
        {
            Text = "Keep changes in Stash and restore them next time.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Layout.Body = names;
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
