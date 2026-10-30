using Invoklet.Languages.CSharp.IntelliSense.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Syntax;

public sealed class SourcePositionsTests
{
    private static bool InComment(string code, int position)
        => SourcePositions.IsInsideComment(CSharpSyntaxTree.ParseText(code).GetRoot(), position);

    private static bool InLiteral(string code, int position)
        => SourcePositions.IsInsideStringOrCharLiteral(CSharpSyntaxTree.ParseText(code).GetRoot(), position);

    [Theory]
    [InlineData("var a = 1; // note", 14)]
    [InlineData("var a = 1; // note", 18)]
    [InlineData("var a = /* note */ 1;", 14)]
    [InlineData("/// <summary>x</summary>\nvoid M() { }", 8)]
    public void IsInsideComment_ACaretInACommentOrAtItsEnd_IsTrue(string code, int position)
        => Assert.True(InComment(code, position));

    [Theory]
    [InlineData("var a = 1; // note", 4)]
    [InlineData("var a = \"//\";", 4)]
    public void IsInsideComment_ACaretInCode_IsFalse(string code, int position)
        => Assert.False(InComment(code, position));

    [Theory]
    [InlineData("var a = \"text\";", 12)]
    [InlineData("var a = \"text", 13)]
    [InlineData("var a = 'c';", 10)]
    [InlineData("var a = \"\"\"raw\"\"\";", 13)]
    public void IsInsideStringOrCharLiteral_ACaretInALiteral_IsTrue(string code, int position)
        => Assert.True(InLiteral(code, position));

    [Fact]
    public void IsInsideStringOrCharLiteral_AnInterpolationHole_IsFalseSoCompletionStillWorksThere()
        => Assert.False(InLiteral("var a = $\"x{ba}\";", 14));

    [Fact]
    public void IsInsideStringOrCharLiteral_PlainCode_IsFalse()
        => Assert.False(InLiteral("var a = 1;", 5));
}
