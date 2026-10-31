using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
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
        new(QuickOpenKind.Script, "/s/Alpha.inv", "Alpha", "", -1, new DateTime(2026, 10, 30, 9, 15, 0)),
        new(QuickOpenKind.Script, "/s/Beta.inv", "Beta", "", 0),
        new(QuickOpenKind.Script, "/s/Gamma.inv", "Gamma", "sales", -1),
        new(QuickOpenKind.Sample, "quick-start.inv", "Quick start", "Getting started", -1),
    ];

    private static readonly QuickOpenEntry Notes = new(QuickOpenKind.OtherFile, "/s/notes.md", "notes.md", "", -1);

    private static QuickOpenContent Content(string textOfGamma = "nothing here") => new(
        Entries,
        _ => Task.FromResult<IReadOnlyList<QuickOpenEntry>>([Notes]),
        (entry, _) => Task.FromResult<string?>(entry.Name == "Gamma" ? textOfGamma : "text"),
        "Ctrl+,");

    private static async Task<QuickOpenEntry?> UseAsync(Action<QuickOpenDialog> act, QuickOpenContent? content = null)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new QuickOpenDialog(content ?? Content());
        try
        {
            var result = dialog.ShowDialog<QuickOpenEntry?>(owner);
            Dispatcher.UIThread.RunJobs();
            act(dialog);
            Dispatcher.UIThread.RunJobs();
            if (!result.IsCompleted) dialog.Close(null);
            return await result;
        }
        finally
        {
            // Whatever the test did or failed to do, no window outlives it.
            dialog.Close(null);
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheBox_Should_ListTheRecentScriptFirstAndOpenItOnEnter()
    {
        var chosen = await UseAsync(d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None));

        Assert.Equal("/s/Beta.inv", chosen!.Target);
    }

    [AvaloniaFact]
    public async Task Typing_Should_NarrowTheList_AndEnterOpensTheMatch()
    {
        var chosen = await UseAsync(d =>
        {
            d.FindControl<TextBox>("QueryBox")!.Text = "gam";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, d.FindControl<ListBox>("ResultList")!.ItemCount);
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("/s/Gamma.inv", chosen!.Target);
    }

    [AvaloniaFact]
    public async Task TheArrows_Should_MoveTheSelection_WhileTheTextBoxKeepsTheFocus()
    {
        var chosen = await UseAsync(d =>
        {
            d.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(d.FindControl<TextBox>("QueryBox")!.IsFocused);
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("/s/Alpha.inv", chosen!.Target);
    }

    [AvaloniaFact]
    public async Task Escape_Should_CloseTheBoxWithoutAScript()
    {
        var chosen = await UseAsync(d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));

        Assert.Null(chosen);
    }

    [AvaloniaFact]
    public async Task NothingMatching_Should_ShowAMessage_AndEnterOpensNothing()
    {
        string? shown = null;
        var chosen = await UseAsync(d =>
        {
            d.FindControl<TextBox>("QueryBox")!.Text = "zzz";
            Dispatcher.UIThread.RunJobs();
            shown = d.FindControl<TextBlock>("EmptyText")!.IsVisible ? "visible" : "hidden";
            d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });

        Assert.Equal("visible", shown);
        Assert.Null(chosen);
    }

    // The selected row and the list use the colours of the application's theme, not the platform's blue and neutral grey.
    [AvaloniaFact]
    public void TheList_Should_UseTheThemesColours_ForTheSelectedRowAndTheBackground()
    {
        var theme = new InvokletTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        global::Avalonia.Application.Current!.RequestedThemeVariant = InvokletThemeVariants.InvokletMidnight;
        var dialog = new QuickOpenDialog(Content());
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var list = dialog.FindControl<ListBox>("ResultList")!;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsSelected);
            var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

            Assert.True(theme.TryGetResource("Invoklet.ToolTab.SelectedBackgroundBrush", InvokletThemeVariants.InvokletMidnight, out var selected));
            Assert.True(theme.TryGetResource("Invoklet.Editor.BackgroundBrush", InvokletThemeVariants.InvokletMidnight, out var background));
            Assert.Equal(((ISolidColorBrush)selected!).Color, ((ISolidColorBrush)presenter.Background!).Color);
            Assert.Equal(((ISolidColorBrush)background!).Color, ((ISolidColorBrush)list.Background!).Color);
        }
        finally
        {
            dialog.Close();
            global::Avalonia.Application.Current!.RequestedThemeVariant = null;
            global::Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static string[] Names(QuickOpenDialog d)
        => [.. d.FindControl<ListBox>("ResultList")!.Items.OfType<QuickOpenEntry>().Select(e => e.Name)];

    [AvaloniaFact]
    public async Task TheTitle_Should_NameTheKey()
    {
        string? title = null;
        await UseAsync(d => title = d.FindControl<TextBlock>("TitleText")!.Text);

        Assert.Equal("Quick Open (Ctrl+,)", title);
    }

    [AvaloniaFact]
    public async Task TheSamples_Should_BeListedWithMyScripts_AndEitherCanBeSwitchedOff()
    {
        string[]? both = null, withoutScripts = null, withoutSamples = null;
        await UseAsync(d =>
        {
            both = Names(d);
            d.FindControl<CheckBox>("ScriptsCheck")!.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            withoutScripts = Names(d);
            d.FindControl<CheckBox>("ScriptsCheck")!.IsChecked = true;
            d.FindControl<CheckBox>("SamplesCheck")!.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            withoutSamples = Names(d);
        });

        Assert.Equal(["Beta", "Alpha", "Gamma", "Quick start"], both!);
        Assert.Equal(["Quick start"], withoutScripts!);
        Assert.Equal(["Beta", "Alpha", "Gamma"], withoutSamples!);
    }

    [AvaloniaFact]
    public async Task TheOtherFiles_Should_AppearOnlyAfterTheyAreIncluded()
    {
        string[]? before = null, after = null;
        await UseAsync(d =>
        {
            before = Names(d);
            d.FindControl<CheckBox>("OtherFilesCheck")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
        });
        // The files are listed by a task: wait for it, with the box still open.
        var owner = new Window();
        owner.Show();
        var dialog = new QuickOpenDialog(Content());
        try
        {
            _ = dialog.ShowDialog<QuickOpenEntry?>(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.FindControl<CheckBox>("OtherFilesCheck")!.IsChecked = true;
            for (var i = 0; i < 200 && !Names(dialog).Contains("notes.md"); i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            after = Names(dialog);
        }
        finally
        {
            dialog.Close(null);
            owner.Close();
        }

        Assert.DoesNotContain("notes.md", before!);
        Assert.Contains("notes.md", after);
    }

    [AvaloniaFact]
    public async Task TheClearButton_Should_AppearWithText_AndEmptyTheBox()
    {
        bool? visibleEmpty = null, visibleTyped = null;
        string? afterClear = null;
        await UseAsync(d =>
        {
            var clear = d.FindControl<Button>("ClearButton")!;
            visibleEmpty = clear.IsVisible;
            d.FindControl<TextBox>("QueryBox")!.Text = "gam";
            Dispatcher.UIThread.RunJobs();
            visibleTyped = clear.IsVisible;
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            afterClear = d.FindControl<TextBox>("QueryBox")!.Text;
        });

        Assert.False(visibleEmpty);
        Assert.True(visibleTyped);
        Assert.Equal("", afterClear);
    }

    [AvaloniaFact]
    public async Task TheOpenButton_Should_OpenTheSelectedRow_AndCancelClosesWithoutOne()
    {
        var opened = await UseAsync(d =>
            d.FindControl<Button>("OpenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        var cancelled = await UseAsync(d =>
            d.GetLogicalDescendants().OfType<Button>().Single(b => (string?)b.Content == "Cancel")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        Assert.Equal("/s/Beta.inv", opened!.Target);
        Assert.Null(cancelled);
    }

    [AvaloniaFact]
    public async Task TheOpenButton_Should_BeDisabled_WhenNothingMatches()
    {
        bool? enabled = null;
        await UseAsync(d =>
        {
            d.FindControl<TextBox>("QueryBox")!.Text = "zzz";
            Dispatcher.UIThread.RunJobs();
            enabled = d.FindControl<Button>("OpenButton")!.IsEnabled;
        });

        Assert.False(enabled);
    }

    [AvaloniaFact]
    public async Task SearchInside_Should_AddTheScriptsWhoseTextHoldsWhatWasTyped()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new QuickOpenDialog(Content(textOfGamma: "var needle = 1;"));
        string[] names = [];
        try
        {
            _ = dialog.ShowDialog<QuickOpenEntry?>(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.FindControl<CheckBox>("SearchInsideCheck")!.IsChecked = true;
            dialog.FindControl<TextBox>("QueryBox")!.Text = "needle";
            for (var i = 0; i < 300 && !names.Contains("Gamma"); i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                names = Names(dialog);
            }
        }
        finally
        {
            dialog.Close(null);
            owner.Close();
        }

        Assert.Equal(["Gamma"], names);
    }

    [AvaloniaFact]
    public async Task TheRows_Should_ShowTheFolderAndTheLastModified()
    {
        string? modified = null, folder = null;
        await UseAsync(d =>
        {
            var texts = d.GetVisualDescendants().OfType<TextBlock>().Select(b => b.Text).ToList();
            modified = texts.FirstOrDefault(x => x is not null && x.Contains("2026", StringComparison.Ordinal));
            folder = texts.FirstOrDefault(x => x == "sales");
        });

        Assert.NotNull(modified);
        Assert.Equal("sales", folder);
    }

    [AvaloniaFact]
    public async Task CancelAndOpen_Should_NotTouch()
    {
        double gap = 0;
        await UseAsync(d =>
        {
            var cancel = d.GetLogicalDescendants().OfType<Button>().Single(b => (string?)b.Content == "Cancel");
            var open = d.FindControl<Button>("OpenButton")!;
            gap = open.Bounds.Left - cancel.Bounds.Right;
        });

        Assert.True(gap >= 8, $"the buttons are {gap} px apart");
    }
}
