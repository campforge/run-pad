using Invoklet.Languages.CSharp.IntelliSense.Completion;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Where the editor offers the property template: inside an empty class/struct/record/interface
// body, and nowhere else. '|' marks the position.
public class CSharpMemberPositionTests
{
    private static bool At(string marked)
        => CSharpMemberPosition.IsInEmptyTypeBody(marked.Replace("|", ""), marked.IndexOf('|'));

    [Theory]
    [InlineData("class C\n{\n    |\n}")]
    [InlineData("class C\n{\n|")] // just typed "{" and Enter, no closing brace yet
    [InlineData("public struct S\n{\n    |\n}")]
    [InlineData("record R\n{\n    |\n}")]
    [InlineData("interface I\n{\n    |\n}")]
    [InlineData("namespace N;\nclass Outer\n{\n    class Inner\n    {\n        |\n    }\n}")]
    [InlineData("Console.WriteLine(1);\n\nclass C\n{\n    |\n}")] // a Statements script
    [InlineData("void Main()\n{\n}\n\nclass C\n{\n    |\n}")] // a Program script
    public void Should_Offer_InAnEmptyTypeBody(string marked) => Assert.True(At(marked));

    [Theory]
    [InlineData("class C\n{\n    int X;\n    |\n}")] // the type already has members
    [InlineData("class C\n{\n    void M()\n    {\n        |\n    }\n}")] // a method body
    [InlineData("class C\n{\n    void M()\n    {\n|")] // an unfinished method body
    [InlineData("var p = new P\n{\n    |\n};")] // an object initializer
    [InlineData("class C\n{\n    /*\n    |\n    */\n}")] // a comment
    [InlineData("class C\n{\n#if NEVER\n    |\n#endif\n}")] // disabled code
    [InlineData("var s = @\"\n|\n\";\nclass C { }")] // a string
    [InlineData("enum E\n{\n    |\n}")] // enum members aren't properties
    [InlineData("|class C\n{\n}")] // before the type
    [InlineData("class C\n{\n}\n|")] // after it
    [InlineData("Console.WriteLine(1);\n|")]
    public void Should_NotOffer_Elsewhere(string marked) => Assert.False(At(marked));

    [Fact]
    public void Should_IgnoreAPositionOutsideTheText()
    {
        Assert.False(CSharpMemberPosition.IsInEmptyTypeBody("class C { }", -1, TestContext.Current.CancellationToken));
        Assert.False(CSharpMemberPosition.IsInEmptyTypeBody("class C { }", 100, TestContext.Current.CancellationToken));
    }
}
