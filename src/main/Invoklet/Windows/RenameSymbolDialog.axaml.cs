using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Invoklet.Windows;

public partial class RenameSymbolDialog : Window
{
    public RenameSymbolDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateOkEnabled();
    }

    // "Smart" only in the sense of not offering to commit something that can't possibly be a legal
    // identifier — a name that's syntactically fine but happens to collide with something (a
    // reserved keyword, an existing symbol) still goes through, and shows up as a live diagnostic
    // squiggle afterward (CSharpIntelliSenseService's own existing diagnostics pass) rather than
    // being caught here — reimplementing full identifier/keyword-escaping validation in the dialog
    // would just duplicate what that pass already does correctly.
    private static bool IsValidIdentifier(string? name) =>
        !string.IsNullOrEmpty(name)
            && (char.IsLetter(name[0]) || name[0] == '_')
            && name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateOkEnabled();

    private void UpdateOkEnabled() => OkButton.IsEnabled = IsValidIdentifier(NameBox.Text);

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close(NameBox.Text);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
