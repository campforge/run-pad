using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #83: breakpoints used to render as the exact same filled disc for every state, just
// recolored (Verified/Unverified/Pending all one shape, three colors; Condition/HitCondition not
// reflected at all). BreakPointMargin.ComputeBreakpointIconPlan is the pulled-out decision — pure
// function of IBreakpoint, no live DrawingContext/TextView needed — so this exercises the actual new
// branching without a rendering harness (same "extract the decision from the drawing" precedent as
// CodeFixMarginTests/ComputeIconTransform, TODO #75/#82).
public class BreakPointMarginTests
{
    private static Breakpoint MakeBreakpoint(
        BreakpointVerificationState state = BreakpointVerificationState.Pending,
        string? condition = null,
        string? hitCondition = null) => new()
        {
            Line = 1,
            VerificationState = state,
            Condition = condition,
            HitCondition = hitCondition,
        };

    [Theory]
    [InlineData(BreakpointVerificationState.Pending)]
    [InlineData(BreakpointVerificationState.Verified)]
    public void ComputeBreakpointIconPlan_Should_NotDrawOutline_ForPendingOrVerified(
        BreakpointVerificationState state)
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(MakeBreakpoint(state));

        Assert.False(plan.DrawOutline);
    }

    [Fact]
    public void ComputeBreakpointIconPlan_Should_DrawOutline_ForUnverified()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(
            MakeBreakpoint(BreakpointVerificationState.Unverified));

        Assert.True(plan.DrawOutline);
    }

    [Fact]
    public void ComputeBreakpointIconPlan_Should_NotDrawConditionBadge_ForAPlainBreakpoint()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(MakeBreakpoint());

        Assert.False(plan.DrawConditionBadge);
    }

    [Fact]
    public void ComputeBreakpointIconPlan_Should_DrawConditionBadge_WhenConditionIsSet()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(MakeBreakpoint(condition: "x > 5"));

        Assert.True(plan.DrawConditionBadge);
    }

    [Fact]
    public void ComputeBreakpointIconPlan_Should_DrawConditionBadge_WhenHitConditionIsSet()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(MakeBreakpoint(hitCondition: "= 5"));

        Assert.True(plan.DrawConditionBadge);
    }

    // Both aspects are independent — an unverified breakpoint that's also conditional should still
    // get the badge, not have one state silently override the other.
    [Fact]
    public void ComputeBreakpointIconPlan_Should_CombineOutlineAndBadge_ForAnUnverifiedConditionalBreakpoint()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(
            MakeBreakpoint(BreakpointVerificationState.Unverified, condition: "x > 5"));

        Assert.True(plan.DrawOutline);
        Assert.True(plan.DrawConditionBadge);
    }
}
