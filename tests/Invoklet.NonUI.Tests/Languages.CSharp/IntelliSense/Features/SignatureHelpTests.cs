using Invoklet.Languages.CSharp.IntelliSense.Syntax;
using Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Features.Fixtures;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Features;

public sealed class SignatureHelpTests
{
    private const string Methods = "class C { public void M(int a, int b) { } public void M(int a) { } }\n";

    [Fact]
    public async Task GetSignatureHelpAsync_InsideTheArgumentsOfACall_OffersTheOverloadsAndTheArgumentIndex()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateSignatureHelp();
        var code = Methods + "new C().M(1, ";

        // Act
        var help = await sut.GetSignatureHelpAsync(code, code.Length, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(help);
        Assert.Equal(2, help.Items.Count);
        Assert.Equal(1, help.ArgumentIndex);
        Assert.True(help.IsArgumentSlotEmpty);
    }

    [Fact]
    public async Task GetSignatureHelpAsync_TwoArguments_SelectsTheOverloadThatTakesTwo()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateSignatureHelp();
        var code = Methods + "new C().M(1, 2";

        // Act
        var help = await sut.GetSignatureHelpAsync(code, code.Length, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, help!.Items[help.SelectedItemIndex].Parameters.Count);
    }

    [Fact]
    public async Task GetSignatureHelpAsync_OutsideAnyCall_IsNull()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateSignatureHelp();

        // Act
        var help = await sut.GetSignatureHelpAsync(Methods + "var x = 1;", 3, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(help);
    }

    [Theory]
    [InlineData("M(1, 2, 3)", 4, 1)]
    [InlineData("M(1, 2, 3)", 2, 0)]
    [InlineData("M()", 2, 0)]
    public void CountArgumentsBefore_APosition_IsTheNumberOfCommasBeforeIt(string code, int position, int expected)
    {
        // Arrange
        var list = CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ArgumentListSyntax>().First();

        // Act
        var count = SourcePositions.CountArgumentsBefore(list, position);

        // Assert
        Assert.Equal(expected, count);
    }

    [Fact]
    public void IsInsideArgumentList_BetweenTheParentheses_IsTrueAndBeforeTheOpeningOneFalse()
    {
        // Arrange
        var list = CSharpSyntaxTree.ParseText("M(1)", cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ArgumentListSyntax>().First();

        // Act
        var inside = SourcePositions.IsInsideArgumentList(list, 2);
        var before = SourcePositions.IsInsideArgumentList(list, 0);

        // Assert
        Assert.True(inside);
        Assert.False(before);
    }
}
