using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Completion;

public sealed class CompletionHelpersTests
{
    private static SimpleCompletionItem Item(string text, CompletionItemKind kind, bool own = false)
        => new(text, kind, [], null, text, 0, [], IsOwnMember: own);

    [Theory]
    [InlineData("override", 8, true)]
    [InlineData("public override  ", 16, true)]
    [InlineData("overrides", 9, false)]
    [InlineData("myoverride", 10, false)]
    [InlineData("over", 4, false)]
    public void IsAfterOverrideModifier_TheTextBeforeTheCaret_IsTrueOnlyForTheWholeKeyword(string code, int position, bool expected)
        => Assert.Equal(expected, OverrideStubs.IsAfterOverrideModifier(code, position));

    [Theory]
    [InlineData("var x = foo", 11, "foo")]
    [InlineData("var x = foo.ba", 14, "ba")]
    [InlineData("x ", 2, "")]
    public void GetIdentifierPrefix_TheTextBeforeTheCaret_IsTheIdentifierBeingTyped(string code, int position, string expected)
        => Assert.Equal(expected, CompletionItems.GetIdentifierPrefix(code, position));

    [Fact]
    public void GroupByDisplayText_SeveralMethodsWithOneName_AreOneItemThatCountsTheOverloads()
    {
        // Arrange
        var items = new ICompletionItem[]
        {
            Item("Write", CompletionItemKind.Method),
            Item("Write", CompletionItemKind.Method),
            Item("Write", CompletionItemKind.Method, own: true),
            Item("Read", CompletionItemKind.Method),
        };

        // Act
        var grouped = CompletionItems.GroupByDisplayText(items);

        // Assert
        Assert.Equal(2, grouped.Count);
        var write = Assert.Single(grouped, i => i.DisplayText == "Write");
        Assert.Equal(3, write.OverloadCount);
        Assert.True(write.IsOwnMember);
        Assert.Equal(1, grouped.Single(i => i.DisplayText == "Read").OverloadCount);
    }

    [Fact]
    public void ScopeRankPriority_KeywordsAreTheLast_AndASymbolOfTheScopeAlwaysComesBefore()
        => Assert.True(CompletionItems.ScopeRankPriority(0) > CompletionItems.ScopeRankPriority(9));

    [Theory]
    [InlineData("from", true, false)]
    [InlineData("orderby", true, false)]
    [InlineData("int", false, true)]
    public void IsKeywordApplicable_ATypeArgumentPosition_AllowsOnlyThePredefinedTypes(string keyword, bool isTypeArgument, bool expected)
    {
        // Arrange
        var root = CSharpSyntaxTree.ParseText("var a = new List<", cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

        // Act
        var applicable = CompletionKeywords.IsKeywordApplicable(Item(keyword, CompletionItemKind.Keyword), root, 17, isTypeArgument);

        // Assert
        Assert.Equal(expected, applicable);
    }

    [Fact]
    public void IsKeywordApplicable_AQueryKeywordOutsideAQuery_IsHidden()
    {
        // Arrange
        var root = CSharpSyntaxTree.ParseText("var a = 1;", cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

        // Act
        var applicable = CompletionKeywords.IsKeywordApplicable(Item("orderby", CompletionItemKind.Keyword), root, 5, false);

        // Assert
        Assert.False(applicable);
    }

    [Fact]
    public void IsKeywordApplicable_APlainKeyword_IsAlwaysOffered()
    {
        // Arrange
        var root = CSharpSyntaxTree.ParseText("var a = 1;", cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

        // Act
        var applicable = CompletionKeywords.IsKeywordApplicable(Item("while", CompletionItemKind.Keyword), root, 5, false);

        // Assert
        Assert.True(applicable);
    }
}
