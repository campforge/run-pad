using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Invoklet.Services.QuickOpen;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows;

// Quick Open: typing narrows the list, the arrows move through it while the text box keeps the focus, Enter opens the selected
// script and Escape closes the box without one.
public class QuickOpenDialogTests
{
    private static readonly QuickOpenEntry[] Entries =
    [
        new("/s/Alpha.inv", "Alpha", "", -1),
        new("/s/Beta.inv", "Beta", "", 0),
        new("/s/Gamma.inv", "Gamma", "sales", -1),
    ];

    private static async Task<string?> UseAsync(Action<QuickOpenDialog> act)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new QuickOpenDialog(Entries);
        var result = dialog.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();
        act(dialog);
        Dispatcher.UIThread.RunJobs();
        if (!result.IsCompleted) dialog.Close(null);
        var path = await result;
        owner.Close();
        return path;
    }

    [AvaloniaFact]
    public async Task TheBox_Should_ListTheRecentScriptFirstAndOpenItOnEnter()
    {
        var path = await UseAsync(d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None));

        Assert.Equal("/s/Beta.inv", path);
    }

    [AvaloniaFact]
    public async Task Typing_Should_NarrowTheList_AndEnterOpensTheMatch()
    {
        var path = await UseAsync(d =>
        {
            d.FindControl<TextBox>("QueryBox")!.Text = "gam";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, d.FindControl<ListBox>("ResultList")!.ItemCount);
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("/s/Gamma.inv", path);
    }

    [AvaloniaFact]
    public async Task TheArrows_Should_MoveTheSelection_WhileTheTextBoxKeepsTheFocus()
    {
        var path = await UseAsync(d =>
        {
            d.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(d.FindControl<TextBox>("QueryBox")!.IsFocused);
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("/s/Alpha.inv", path);
    }

    [AvaloniaFact]
    public async Task Escape_Should_CloseTheBoxWithoutAScript()
    {
        var path = await UseAsync(d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));

        Assert.Null(path);
    }

    [AvaloniaFact]
    public async Task NothingMatching_Should_ShowAMessage_AndEnterOpensNothing()
    {
        string? shown = null;
        var path = await UseAsync(d =>
        {
            d.FindControl<TextBox>("QueryBox")!.Text = "zzz";
            Dispatcher.UIThread.RunJobs();
            shown = d.FindControl<TextBlock>("EmptyText")!.IsVisible ? "visible" : "hidden";
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("visible", shown);
        Assert.Null(path);
    }
}
