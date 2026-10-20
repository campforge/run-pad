using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RunPad.Contracts.IntelliSense;
using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class BreakpointConditionEditorTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enter_Should_ApplyValidatedCondition_AndEscape_Cancel(bool cancel)
    {
        using var editor = new BreakpointConditionEditor("i == 2", "% 2", true,
            (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(new(null, [])));
        var host = new Window { Content = editor };
        (string? Condition, string? HitCondition, bool Enabled)? answer = null;
        var finished = false;
        editor.Finished += value => { answer = value; finished = true; };
        host.Show();
        try
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
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
            (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(new("Incomplete condition", ["count"])));
        var host = new Window { Content = editor };
        var finished = false;
        editor.Finished += _ => finished = true;
        host.Show();
        try
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            host.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            host.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("count", editor.FindControl<TextBox>("ConditionBox")!.Text);
            Assert.False(finished);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidCondition_Should_ShowError_AndPreventApply()
    {
        using var editor = new BreakpointConditionEditor("unknown > 2", null, true,
            (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(new("Unknown identifier", [])));
        var host = new Window { Content = editor };
        var finished = false;
        editor.Finished += _ => finished = true;
        host.Show();
        try
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(editor.FindControl<Button>("DoneButton")!.IsEnabled);
            Assert.Equal("Unknown identifier", editor.FindControl<TextBlock>("ErrorText")!.Text);
            host.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.False(finished);
        }
        finally { host.Close(); }
    }
}
