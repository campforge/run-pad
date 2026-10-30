using AvaloniaEdit.CodeCompletion;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The completion list's CamelHumps match: each part of the query starts the next word of the name.
public class CompletionListCamelHumpsTests
{
    [Theory]
    [InlineData("ArgumentsEnvironment", "ArgEnv")]
    [InlineData("ArgumentsEnvironment", "ae")]
    [InlineData("ArgumentsEnvironment", "argumentsenv")]
    [InlineData("StringBuilder", "SB")]
    [InlineData("StringBuilder", "SBu")]
    [InlineData("StringBuilder", "strbui")]
    [InlineData("CodeQualityAnalysis", "CQ")]
    [InlineData("HTTPClient", "HC")]
    [InlineData("HTTPClient", "HTTPCl")]
    [InlineData("max_retry_count", "mrc")]
    [InlineData("camelCaseText", "cct")]
    public void CamelCaseMatch_Should_Match_WordStartsInOrder(string text, string query)
    {
        Assert.True(CompletionList.CamelCaseMatch(text, query));
    }

    [Theory]
    [InlineData("ArgumentsEnvironment", "EnvArg")]
    [InlineData("ArgumentsEnvironment", "rgEnv")]
    [InlineData("StringBuilder", "SBx")]
    [InlineData("CodeQualityAnalysis", "CA")]
    [InlineData("StringBuilder", "")]
    public void CamelCaseMatch_Should_NotMatch_OutOfOrderSkippedOrMidWord(string text, string query)
    {
        Assert.False(CompletionList.CamelCaseMatch(text, query));
    }

    // Every "Aa" word can take one or two letters of the query, so a failing query has
    // exponentially many ways to split over the words unless tried splits are remembered.
    [Fact]
    public void CamelCaseMatch_Should_FailFast_When_ManySplitsAreTried()
    {
        var text = string.Concat(Enumerable.Repeat("Aa", 40));
        var query = new string('a', 60) + "x";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var matched = CompletionList.CamelCaseMatch(text, query);
        stopwatch.Stop();

        Assert.False(matched);
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Took {stopwatch.ElapsedMilliseconds} ms");
    }
}
