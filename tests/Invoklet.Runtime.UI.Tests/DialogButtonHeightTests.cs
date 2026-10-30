using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The buttons of the dialogs are as tall as the fields beside them (the theme's control height), whatever the dialog sets or
// leaves out, so a row of buttons and the buttons of two dialogs line up.
public class DialogButtonHeightTests
{
    public static TheoryData<string> Dialogs => new("error", "newfile", "goto", "reload", "revert", "exit", "conflict", "unsaved", "rename", "connection", "about");

    private static Window Create(string kind) => kind switch
    {
        "error" => new ErrorDialog("Title", "A message"),
        "newfile" => new NewFileKindDialog(),
        "goto" => new GoToLineDialog(1, 10),
        "reload" => ReloadFileDialog.ChangedOnDisk("a.inv"),
        "revert" => ReloadFileDialog.Revert("a.inv"),
        "exit" => new ExitUnsavedDialog(["a.inv", "b.inv"]),
        "conflict" => new SaveConflictDialog("a.inv"),
        "unsaved" => new UnsavedChangesDialog("a.inv"),
        "rename" => new RenameSymbolDialog("Name"),
        "connection" => new ConnectionPackageConfirmationDialog("Add it?"),
        _ => new AboutWindow(),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public void EveryButtonOfADialog_Should_BeAsTallAsTheThemesControlHeight(string kind)
    {
        var theme = new InvokletTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var window = Create(kind);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            theme.TryGetResource("Invoklet.Size.Control", null, out var expected);

            var heights = window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible)
                .Select(b => $"{b.Name ?? b.Content}: {b.Bounds.Height}").ToList();

            Assert.NotEmpty(heights);
            Assert.All(window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible),
                b => Assert.True(b.Bounds.Height == (double)expected!, $"{kind}: {string.Join("; ", heights)}"));
        }
        finally
        {
            window.Close();
            global::Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }
}
