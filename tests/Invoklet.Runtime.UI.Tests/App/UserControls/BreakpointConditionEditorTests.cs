using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.UserControls;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

public class BreakpointConditionEditorTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enter_Should_ApplyValidatedCondition_AndEscape_Cancel(bool cancel)
    {
        using var editor = new BreakpointConditionEditor("i == 2", "% 2", true,
            (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(new(null, [])),
            validationDelay: TimeSpan.Zero);
        var host = new Window { Content = editor };
        (string? Condition, string? HitCondition, bool Enabled)? answer = null;
        var finished = false;
        editor.Finished += value => { answer = value; finished = true; };
        host.Show();
        try
        {
            await TestWait.WhileAsync(() => editor.FindControl<TextBlock>("ErrorText")!.Text is null);
            Assert.True(editor.FindControl<Button>("DoneButton")!.IsEnabled);
            host.KeyPressQwerty(cancel ? PhysicalKey.Escape : PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(finished);
            if (cancel) Assert.Null(answer);
            else
            {
                Assert.Equal("i == 2", answer!.Value.Condition);
                Assert.Equal("% 2", answer.Value.HitCondition);
            }
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task Suggestions_Should_CompleteWithDownAndEnter_WithoutApplyingTheBreakpoint()
    {
        using var editor = new BreakpointConditionEditor("cou", null, true,
            (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(new("Incomplete condition", ["count"])),
            validationDelay: TimeSpan.Zero);
        var host = new Window { Content = editor };
        var finished = false;
        editor.Finished += _ => finished = true;
        host.Show();
        try
        {
            var suggestions = editor.FindControl<ListBox>("Suggestions")!;
            await TestWait.WhileAsync(() => !suggestions.IsVisible || suggestions.ItemCount == 0);
            host.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            host.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("count", editor.FindControl<TextBox>("ConditionBox")!.Text);
            Assert.False(finished);
        }
        finally { host.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task InvalidCondition_Should_ShowError_AndPreventApply(int analysisDelay)
    {
        using var editor = new BreakpointConditionEditor("unknown > 2", null, true,
            async (_, ct) =>
            {
                await Task.Delay(analysisDelay, ct);
                return new("Unknown identifier", []);
            },
            validationDelay: TimeSpan.Zero);
        var host = new Window { Content = editor };
        var finished = false;
        editor.Finished += _ => finished = true;
        host.Show();
        try
        {
            await TestWait.WhileAsync(() => editor.FindControl<TextBlock>("ErrorText")!.Text is null);
            Assert.False(editor.FindControl<Button>("DoneButton")!.IsEnabled);
            Assert.Equal("Unknown identifier", editor.FindControl<TextBlock>("ErrorText")!.Text);
            host.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.False(finished);
        }
        finally { host.Close(); }
    }
}
