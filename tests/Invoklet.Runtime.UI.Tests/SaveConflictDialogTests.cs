using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Invoklet.Services;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The question asked before replacing a file changed on disk: Enter keeps both versions (Save As…),
// replacing the file is never the default, and Escape or closing the window cancels.
public class SaveConflictDialogTests
{
    private static async Task<SaveConflictChoice?> AnswerAsync(Action<SaveConflictDialog> answer)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new SaveConflictDialog("demo.rpad");
        var result = dialog.ShowDialog<SaveConflictChoice?>(owner);
        Dispatcher.UIThread.RunJobs();
        answer(dialog);
        Dispatcher.UIThread.RunJobs();
        var choice = await result;
        owner.Close();
        return choice;
    }

    [AvaloniaFact]
    public void TheDialog_Should_NameTheFile_AndMakeSaveAsTheDefault()
    {
        var dialog = new SaveConflictDialog("demo.rpad");
        var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToDictionary(b => (string)b.Content!);

        Assert.Contains("\"demo.rpad\" was changed by another program", dialog.FindControl<TextBlock>("MessageBlock")!.Text);
        Assert.Equal(["Save As…", "Overwrite", "Cancel"], buttons.Keys);
        Assert.True(buttons["Save As…"].IsDefault);
        Assert.False(buttons["Overwrite"].IsDefault);
        Assert.True(buttons["Cancel"].IsCancel);
    }

    [AvaloniaTheory]
    [InlineData("SaveAsButton", SaveConflictChoice.SaveAs)]
    [InlineData("OverwriteButton", SaveConflictChoice.Overwrite)]
    [InlineData("CancelButton", SaveConflictChoice.Cancel)]
    public async Task EachButton_Should_CloseWithItsChoice(string button, SaveConflictChoice expected)
    {
        var choice = await AnswerAsync(d => d.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        Assert.Equal(expected, choice);
    }

    [AvaloniaFact]
    public async Task Enter_Should_ChooseSaveAs_AndEscape_Cancel()
    {
        Assert.Equal(SaveConflictChoice.SaveAs, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)));
        Assert.Equal(SaveConflictChoice.Cancel, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None)));
    }

    // Closing the window chooses nothing; the window service turns that into Cancel.
    [AvaloniaFact]
    public async Task ClosingTheWindow_Should_ChooseNothing()
    {
        Assert.Null(await AnswerAsync(d => d.Close()));
    }
}
