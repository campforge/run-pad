using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class ConditionDialogTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionEditor_Should_CommitOnEnter_AndCancelOnEscape(bool cancel)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new ConditionDialog("i == 1");
        var result = dialog.ShowDialog<(string? Condition, string? HitCondition, bool Enabled)?>(owner);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var condition = dialog.FindControl<TextBox>("ConditionBox")!;
            Assert.True(condition.IsFocused);
            Assert.False(condition.AcceptsReturn);
            Assert.False(dialog.FindControl<Expander>("HitCountOptions")!.IsExpanded);
            condition.Text = "i == 2";
            dialog.KeyPressQwerty(cancel ? PhysicalKey.Escape : PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var answer = await result.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) Assert.Null(answer);
            else Assert.Equal("i == 2", answer!.Value.Condition);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void ExistingHitCondition_Should_BeVisible()
    {
        var dialog = new ConditionDialog("i > 0", "% 2", false);
        Assert.True(dialog.FindControl<Expander>("HitCountOptions")!.IsExpanded);
        Assert.Equal("% 2", dialog.FindControl<TextBox>("HitConditionBox")!.Text);
        Assert.False(dialog.FindControl<CheckBox>("EnabledBox")!.IsChecked);
    }
}
