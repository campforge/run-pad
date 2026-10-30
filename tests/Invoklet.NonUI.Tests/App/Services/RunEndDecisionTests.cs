using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class RunEndDecisionTests
{
    [Theory]
    [InlineData(true, true, true, true, RunEnd.Cancelled)]
    [InlineData(true, false, false, false, RunEnd.Cancelled)]
    [InlineData(false, true, true, true, RunEnd.UnhandledException)]
    [InlineData(false, true, false, false, RunEnd.UnhandledException)]
    [InlineData(false, false, true, true, RunEnd.Failed)]
    [InlineData(false, false, true, false, RunEnd.Failed)]
    [InlineData(false, false, false, true, RunEnd.ReportedError)]
    [InlineData(false, false, false, false, RunEnd.Succeeded)]
    public void Of_FactsOfARun_PicksTheFirstThatApplies(bool cancelled, bool unhandled, bool failed, bool reported, RunEnd expected)
    {
        // Arrange (the facts are the arguments)

        // Act
        var end = RunEndDecision.Of(cancelled, unhandled, failed, reported);

        // Assert
        Assert.Equal(expected, end);
    }
}
