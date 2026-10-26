using Xunit;

namespace RunPad.Format.RPad.Tests;

// An argument list as one line per argument (the Dependencies window's Arguments tab).
public class RpadArgumentLinesTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("  spaced  \n\"quoted\"", new[] { "  spaced  ", "\"quoted\"" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("\n", new[] { "" })]
    public void Parse_Should_TakeEachLineAsOneArgument(string text, string[] expected)
        => Assert.Equal(expected, RpadArgumentLines.Parse(text));

    public static TheoryData<string[]> Lists => new() { new string[0], new[] { "a" }, new[] { "" }, new[] { "a", "" }, new[] { "", "", "x y", "é" } };

    [Theory]
    [MemberData(nameof(Lists))]
    public void Format_ThenParse_Should_GiveTheSameList(string[] arguments)
    {
        Assert.True(RpadArgumentLines.Fits(arguments));
        Assert.Equal(arguments, RpadArgumentLines.Parse(RpadArgumentLines.Format(arguments)));
    }

    [Fact]
    public void AnArgumentWithALineBreak_Should_NotFit()
        => Assert.False(RpadArgumentLines.Fits(["two\nlines"]));
}
