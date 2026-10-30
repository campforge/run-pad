using Xunit;

namespace Invoklet.NonUI.Tests;

// The user found that completion (and, worse, the commit-on-"(" behavior) treated the NAME being typed for a fresh
// declaration as a reference to complete/replace — e.g.
// typing "void Main" and then "(" replaced "Main" with an unrelated in-scope symbol that happened to
// share a prefix. LookupSymbols/GetSymbolInfo have no notion of "this identifier is being declared,
// not referenced", so the fix is a dedicated syntax check (IsDeclarationNamePosition) that returns
// null completions — and therefore no window for IsCommitCharacter to clobber — whenever the caret
// sits inside the name of a method/class/property/variable/parameter/type-parameter declaration.
public class CSharpIntelliSenseDeclarationNameTests
{
    // The user's own reported reproduction: "void Main" — caret right after "Main" while still typing
    // the method's own name (Program-kind scripts declare exactly this method).
    [Theory]
    [InlineData("void Main")]
    [InlineData("public void Main")]
    [InlineData("static void Main")]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhileTypingAMethodName(string code)
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("public int Value")]
    [InlineData("private string Nam")]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhileTypingAPropertyName(string code)
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhileTypingAClassName()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "private class App";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhileTypingALocalVariableName()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "var myVariabl";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhileTypingAParameterName()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "void Main(string arg";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // Regression guard: the suppression must be scoped to the declared NAME itself, not to the whole
    // declaration — completion inside the method's own body (an ordinary expression position) must
    // keep working exactly as before.
    [Fact]
    public async Task GetCompletionsAsync_Should_StillWork_InsideAMethodBodyAfterItsNameIsTyped()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "void Main()\n{\n    Console.Wr\n}\n";
        var position = code.IndexOf("Console.Wr", StringComparison.Ordinal) + "Console.Wr".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "WriteLine");
    }

    // Also scoped correctly the other way — an ordinary expression position (not any kind of
    // declaration name) must still complete normally; IsDeclarationIdentifierToken only special-cases
    // a specific closed set of syntax nodes, so this guards against it becoming overly broad.
    [Fact]
    public async Task GetCompletionsAsync_Should_StillWork_ForAnOrdinaryExpression()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "Consol";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Console");
    }
}
