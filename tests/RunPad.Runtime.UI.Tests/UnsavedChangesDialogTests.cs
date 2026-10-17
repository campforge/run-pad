using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using RunPad.Services;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class UnsavedChangesDialogTests
{
    private static async Task<UnsavedChangesResult?> AnswerAsync(Action<UnsavedChangesDialog> answer)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new UnsavedChangesDialog("demo.rpad");
        var result = dialog.ShowDialog<UnsavedChangesResult?>(owner);
        Dispatcher.UIThread.RunJobs();
        answer(dialog);
        Dispatcher.UIThread.RunJobs();
        var choice = await result;
        owner.Close();
        return choice;
    }

    [AvaloniaTheory]
    [InlineData("SaveButton", UnsavedChangesResult.Save)]
    [InlineData("DontSaveButton", UnsavedChangesResult.DontSave)]
    [InlineData("CancelButton", UnsavedChangesResult.Cancel)]
    public async Task Buttons_Should_PreserveTheirResults(string name, UnsavedChangesResult expected)
        => Assert.Equal(expected, await AnswerAsync(d => d.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));

    [AvaloniaFact]
    public async Task KeyboardAndWindowClose_Should_PreserveDefaultAndCancel()
    {
        Assert.Equal(UnsavedChangesResult.Save, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)));
        Assert.Equal(UnsavedChangesResult.Cancel, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None)));
        Assert.Null(await AnswerAsync(d => d.Close()));
    }

    [AvaloniaFact]
    public void LongFileName_Should_WrapAndKeepActionsInsideWindow()
    {
        var name = string.Concat(Enumerable.Repeat("long-script-name-", 12)) + ".rpad";
        var dialog = new UnsavedChangesDialog(name);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var layout = dialog.FindControl<DialogLayout>("Layout")!;
            Assert.Equal(name, layout.FileName);
            Assert.True(layout.ShowFileName);
            Assert.True(dialog.Bounds.Height > 150);
            foreach (var button in dialog.GetLogicalDescendants().OfType<Button>())
            {
                Assert.True(button.Bounds.Width >= 72);
                Assert.True(button.Bounds.Height > 0);
                var origin = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= dialog.ClientSize.Width);
                Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= dialog.ClientSize.Height);
            }
            Assert.Equal(WindowDecorations.Full, dialog.WindowDecorations);
            Assert.False(dialog.CanResize);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void NonFilePrompt_Should_NotShowInventedFileName()
    {
        var dialog = UnsavedChangesDialog.WithMessage("Apply changes to these dependencies?");
        var layout = dialog.FindControl<DialogLayout>("Layout")!;
        Assert.False(layout.ShowFileName);
        Assert.Null(layout.FileName);
        Assert.Equal("Apply changes to these dependencies?", layout.Message);
        Assert.True(dialog.FindControl<Button>("SaveButton")!.IsDefault);
        Assert.True(dialog.FindControl<Button>("CancelButton")!.IsCancel);
    }
}
