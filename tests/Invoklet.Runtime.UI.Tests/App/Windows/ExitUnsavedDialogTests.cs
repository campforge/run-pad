using Avalonia;
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

namespace Invoklet.Runtime.UI.Tests.App.Windows;

public class ExitUnsavedDialogTests
{
    private static async Task<ExitUnsavedChoice?> AnswerAsync(Action<ExitUnsavedDialog> answer, params string[] names)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new ExitUnsavedDialog(names.Length == 0 ? ["demo"] : names);
        var result = dialog.ShowDialog<ExitUnsavedChoice?>(owner);
        Dispatcher.UIThread.RunJobs();
        answer(dialog);
        Dispatcher.UIThread.RunJobs();
        var choice = await result;
        owner.Close();
        return choice;
    }

    [AvaloniaTheory]
    [InlineData("SaveAllButton", ExitUnsavedChoice.SaveAll)]
    [InlineData("KeepInStashButton", ExitUnsavedChoice.KeepInStash)]
    [InlineData("CancelButton", ExitUnsavedChoice.Cancel)]
    public async Task Buttons_Should_ReturnTheirChoice(string name, ExitUnsavedChoice expected)
        => Assert.Equal(expected, await AnswerAsync(d => d.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));

    // Closing the dialog any other way never closes the app: the service maps null to Cancel.
    [AvaloniaFact]
    public async Task EnterEscapeAndWindowClose_Should_SaveAll_Cancel_AndCancel()
    {
        Assert.Equal(ExitUnsavedChoice.SaveAll, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)));
        Assert.Equal(ExitUnsavedChoice.Cancel, await AnswerAsync(d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None)));
        Assert.Null(await AnswerAsync(d => d.Close()));
    }

    [AvaloniaFact]
    public async Task Tab_Should_VisitSaveAll_KeepInStash_ThenCancel()
    {
        var focus = new List<string>();
        await AnswerAsync(d =>
        {
            for (var i = 0; i < 4; i++)
            {
                d.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                focus.Add((d.FocusManager?.GetFocusedElement() as Button)?.Name ?? "(none)");
            }
            d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        });
        Assert.Equal(["SaveAllButton", "KeepInStashButton", "CancelButton", "SaveAllButton"], focus);
    }

    [AvaloniaFact]
    public void TheMessage_Should_NameTheTabs_AndStayInsideTheWindow()
    {
        var names = Enumerable.Range(1, 7).Select(i => $"script-with-a-rather-long-name-{i}").ToArray();
        var dialog = new ExitUnsavedDialog(names);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var layout = dialog.FindControl<DialogLayout>("Layout")!;
            Assert.Equal("7 tabs have unsaved changes.", layout.Message);
            // The names scroll inside the window once there are many of them.
            var scroller = Assert.IsType<ScrollViewer>(Assert.IsType<StackPanel>(layout.Body).Children.OfType<ScrollViewer>().Single());
            var rows = Assert.IsType<StackPanel>(scroller.Content).Children.OfType<TextBlock>().ToList();
            Assert.Equal(names.Take(5), rows.Take(5).Select(t => t.Text));
            Assert.Equal("and 2 more", rows[5].Text);
            Assert.Equal("1 tab has unsaved changes.", new ExitUnsavedDialog(["only"]).FindControl<DialogLayout>("Layout")!.Message);
            foreach (var button in dialog.GetLogicalDescendants().OfType<Button>())
            {
                var origin = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(button.Bounds.Width >= 72 && button.Bounds.Height > 0);
                Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= dialog.ClientSize.Width, $"{button.Name} {origin} {button.Bounds} {dialog.ClientSize}");
                Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= dialog.ClientSize.Height);
            }
            Assert.Equal("Unsaved Changes", dialog.Title);
            Assert.False(dialog.CanResize);
        }
        finally { dialog.Close(); }
    }
}
