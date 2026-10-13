using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Breakpoints used to render as the exact same filled disc for every state, just
// recolored (Verified/Unverified/Pending all one shape, three colors; Condition/HitCondition not
// reflected at all). BreakPointMargin.ComputeBreakpointIconPlan is the pulled-out decision — pure
// function of IBreakpoint, no live DrawingContext/TextView needed — so this exercises the actual new
// branching without a rendering harness (same "extract the decision from the drawing" precedent as
// CodeFixMarginTests/ComputeIconTransform).
public class BreakPointMarginTests
{
    private static Breakpoint MakeBreakpoint(
        BreakpointVerificationState state = BreakpointVerificationState.Pending,
        string? condition = null,
        string? hitCondition = null,
        bool enabled = true) => new()
        {
            Line = 1,
            VerificationState = state,
            Condition = condition,
            HitCondition = hitCondition,
            IsEnabled = enabled,
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

    // A disabled breakpoint gets the same contour-only treatment as an unverified
    // one, even when otherwise fully verified — disabled is a reason to hollow the icon on its own.
    [Fact]
    public void ComputeBreakpointIconPlan_Should_DrawOutline_ForADisabledBreakpoint_EvenIfVerified()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(
            MakeBreakpoint(BreakpointVerificationState.Verified, enabled: false));

        Assert.True(plan.DrawOutline);
    }

    [Fact]
    public void ComputeBreakpointIconPlan_Should_NotDrawOutline_ForAnEnabledVerifiedBreakpoint()
    {
        var plan = BreakPointMargin.ComputeBreakpointIconPlan(
            MakeBreakpoint(BreakpointVerificationState.Verified, enabled: true));

        Assert.False(plan.DrawOutline);
    }
}
