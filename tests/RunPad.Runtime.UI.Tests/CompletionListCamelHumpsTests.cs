using AvaloniaEdit.CodeCompletion;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

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
}
