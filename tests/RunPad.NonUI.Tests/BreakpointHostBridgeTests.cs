using Moq;
using RunPad.Contracts;
using RunPad.UserControls.Models;
using Xunit;
using EditorBreakpoint = RunPad.Avalonia.CodeTextEditor.Breakpoint;
using EditorBreakpointContract = RunPad.Avalonia.CodeTextEditor.IBreakpoint;

namespace RunPad.NonUI.Tests;

public sealed class BreakpointHostBridgeTests
{
    [Fact]
    public void Enabled_ExcludesDisabledAndPreservesConditions()
    {
        var enabled = new EditorBreakpoint { Line = 3, Condition = "value > 1", HitCondition = "2", IsEnabled = true };
        var disabled = new EditorBreakpoint { Line = 8, Condition = "ignored", HitCondition = "4", IsEnabled = false };

        var result = BreakpointHostBridge.Enabled([enabled, disabled]);

        Assert.Equal([(3, "value > 1", "2")], result);
    }
    [Fact]
    public void Add_ForwardsLocationAndConditions()
    {
        var host = new Mock<ILanguageHost>();
        IBreakpoint? received = null;
        host.Setup(h => h.AddBreakpoint(It.IsAny<IBreakpoint>()))
            .Callback<IBreakpoint>(breakpoint => received = breakpoint);

        BreakpointHostBridge.Add(host.Object, "demo.rpad", 12, "value > 1", "3");

        host.Verify(h => h.AddBreakpoint(It.IsAny<IBreakpoint>()), Times.Once);
        Assert.NotNull(received);
        Assert.Equal("demo.rpad", received!.FileName);
        Assert.Equal(12, received.Line);
        Assert.Equal("value > 1", received.Condition);
        Assert.Equal("3", received.HitCondition);
    }

    [Fact]
    public void Remove_ForwardsFileNameAndLine()
    {
        var host = new Mock<ILanguageHost>();
        IBreakpoint? received = null;
        host.Setup(h => h.RemoveBreakpoint(It.IsAny<IBreakpoint>()))
            .Callback<IBreakpoint>(breakpoint => received = breakpoint);

        BreakpointHostBridge.Remove(host.Object, "demo.rpad", 12);

        host.Verify(h => h.RemoveBreakpoint(It.IsAny<IBreakpoint>()), Times.Once);
        Assert.NotNull(received);
        Assert.Equal("demo.rpad", received!.FileName);
        Assert.Equal(12, received.Line);
        Assert.Null(received.Condition);
        Assert.Null(received.HitCondition);
    }
}
