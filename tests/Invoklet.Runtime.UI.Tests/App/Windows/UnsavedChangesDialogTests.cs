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

public class UnsavedChangesDialogTests
{
    private static async Task<UnsavedChangesResult?> AnswerAsync(Action<UnsavedChangesDialog> answer)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new UnsavedChangesDialog("demo.inv");
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

    // Keyboard traversal starts on a safe action: nothing is focused when the dialog opens, and Enter
    // presses the focused button, so the first Tab (or Shift+Tab) decides what Enter does next.
    private static async Task<(UnsavedChangesResult? Result, List<string> Focus)> KeysAsync(params (PhysicalKey Key, RawInputModifiers Modifiers)[] keys)
    {
        var focus = new List<string>();
        var result = await AnswerAsync(d =>
        {
            foreach (var (key, modifiers) in keys)
            {
                d.KeyPressQwerty(key, modifiers);
                Dispatcher.UIThread.RunJobs();
                focus.Add((d.FocusManager?.GetFocusedElement() as Button)?.Name ?? "(none)");
            }
        });
        return (result, focus);
    }

    private static readonly (PhysicalKey, RawInputModifiers) Tab = (PhysicalKey.Tab, RawInputModifiers.None);
    private static readonly (PhysicalKey, RawInputModifiers) ShiftTab = (PhysicalKey.Tab, RawInputModifiers.Shift);
    private static readonly (PhysicalKey, RawInputModifiers) Enter = (PhysicalKey.Enter, RawInputModifiers.None);

    [AvaloniaFact]
    public async Task TheFirstTab_ThenEnter_Should_Save()
    {
        var (result, focus) = await KeysAsync(Tab, Enter);

        Assert.Equal("SaveButton", focus[0]);
        Assert.Equal(UnsavedChangesResult.Save, result);
    }

    [AvaloniaFact]
    public async Task TheFirstShiftTab_ThenEnter_Should_Cancel()
    {
        var (result, focus) = await KeysAsync(ShiftTab, Enter);

        Assert.Equal("CancelButton", focus[0]);
        Assert.Equal(UnsavedChangesResult.Cancel, result);
    }

    // Don't Save is still reachable from the keyboard, deliberately: only after the safe first stop.
    [AvaloniaFact]
    public async Task DontSave_Should_BeReachable_AsTheSecondStop()
    {
        var (result, focus) = await KeysAsync(Tab, Tab, Enter);

        Assert.Equal(["SaveButton", "DontSaveButton", "(none)"], focus);
        Assert.Equal(UnsavedChangesResult.DontSave, result);
    }

    [AvaloniaFact]
    public async Task Tab_Should_CycleThroughEveryAction()
    {
        var (_, focus) = await KeysAsync(Tab, Tab, Tab, Tab, (PhysicalKey.Escape, RawInputModifiers.None));

        Assert.Equal(["SaveButton", "DontSaveButton", "CancelButton", "SaveButton"], focus.Take(4));
    }

    [AvaloniaFact]
    public void LongFileName_Should_WrapAndKeepActionsInsideWindow()
    {
        var name = string.Concat(Enumerable.Repeat("long-script-name-", 12)) + ".inv";
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

    // The heading reads as the title, larger than the file name; the file line follows the file name.
    [AvaloniaFact]
    public void TheHeading_Should_BeTheTitle_AndTheFileLine_FollowTheFileName()
    {
        var dialog = new UnsavedChangesDialog("demo.inv");
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var layout = dialog.FindControl<DialogLayout>("Layout")!;
            var heading = layout.FindControl<TextBlock>("HeadingBlock")!;
            var file = layout.FindControl<TextBlock>("FileNameBlock")!;
            Assert.True(heading.FontSize > file.FontSize);
            Assert.True(file.IsVisible);

            layout.FileName = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(file.IsVisible);
            Assert.False(layout.ShowFileName);
        }
        finally { dialog.Close(); }
    }

    // The badge says what kind of dialog it is, in the theme's colours, and is not read out.
    [AvaloniaTheory]
    [InlineData(DialogKind.Question, "?", "Invoklet.Button.Primary.BackgroundBrush")]
    [InlineData(DialogKind.Warning, "!", "Invoklet.Status.PausedBrush")]
    [InlineData(DialogKind.Error, "×", "Invoklet.Status.ErrorBrush")]
    public void TheBadge_Should_FollowTheKind(DialogKind kind, string glyph, string brushKey)
    {
        var dialog = new UnsavedChangesDialog("demo.inv");
        // The test application has no Invoklet theme: give each role its own brush to tell them apart.
        var brushes = new[] { "Invoklet.Button.Primary.BackgroundBrush", "Invoklet.Status.PausedBrush", "Invoklet.Status.ErrorBrush" }
            .ToDictionary(k => k, _ => (global::Avalonia.Media.IBrush)new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.Transparent));
        foreach (var (key, brush) in brushes) dialog.Resources[key] = brush;
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var layout = dialog.FindControl<DialogLayout>("Layout")!;
            Assert.Equal(DialogKind.Question, layout.Kind);
            layout.Kind = kind;
            Dispatcher.UIThread.RunJobs();

            var badge = layout.FindControl<Border>("KindBadge")!;
            Assert.Equal(glyph, layout.FindControl<TextBlock>("KindGlyph")!.Text);
            Assert.Same(brushes[brushKey], badge.Background);
            Assert.Equal(global::Avalonia.Automation.AccessibilityView.Raw, global::Avalonia.Automation.AutomationProperties.GetAccessibilityView(badge));
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
