using Invoklet.Debug.Protocol;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Condition (DAP SourceBreakpoint.condition) worked end-to-end, but HitCondition
// (SourceBreakpoint.hitCondition — a hit-count expression like "= 5"/">= 3"/"% 2") wasn't threaded
// through anywhere despite the DAP protocol type already having the field. These are narrow unit
// tests on the plumbing itself (the gutter-side Breakpoint POCO and the DAP wire type) — an actual
// end-to-end assertion would need a live netcoredbg session, which isn't available in this sandbox.
public class BreakpointHitConditionTests
{
    [Fact]
    public void Breakpoint_Should_StoreHitConditionIndependentlyOfCondition()
    {
        var bp = new Invoklet.Avalonia.CodeTextEditor.Breakpoint { Condition = "x > 5", HitCondition = "% 2" };

        Assert.Equal("x > 5", bp.Condition);
        Assert.Equal("% 2", bp.HitCondition);
    }

    [Fact]
    public void SourceBreakpoint_Should_CarryHitConditionOverTheWire()
    {
        var wireBreakpoint = new SourceBreakpoint(bpLine: 10, condition: "x > 5", hitCondition: ">= 3");

        Assert.Equal(10, wireBreakpoint.line);
        Assert.Equal("x > 5", wireBreakpoint.condition);
        Assert.Equal(">= 3", wireBreakpoint.hitCondition);
    }

    [Fact]
    public void SourceBreakpoint_Should_DefaultHitConditionToNull_WhenOmitted()
    {
        var wireBreakpoint = new SourceBreakpoint(bpLine: 10);

        Assert.Null(wireBreakpoint.hitCondition);
    }
}
