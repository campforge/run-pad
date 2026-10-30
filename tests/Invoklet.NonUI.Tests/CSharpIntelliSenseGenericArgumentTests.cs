using Xunit;

namespace Invoklet.NonUI.Tests;

// Completion right after `List<` (or anywhere inside a generic type argument list) used
// to call LookupSymbols unconditionally and mix in every visible symbol — locals, parameters, methods,
// fields — alongside actual types, exactly like the general "expression position" fallback. Only
// type-ish symbols (named types, type parameters, namespaces — so a namespace can still be typed
// through with '.' to reach a nested type) and the predefined-type keywords are legal there.
public class CSharpIntelliSenseGenericArgumentTests
{
    [Fact]
    public async Task GetCompletionsAsync_Should_NotOffer_LocalVariable_InsideGenericTypeArgument()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C { void M() { int myLocal = 1; var a = new List<\n} }";
        var position = code.IndexOf("List<", StringComparison.Ordinal) + "List<".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == "myLocal");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOffer_ControlFlowKeywords_InsideGenericTypeArgument()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C { void M() { var a = new List<\n} }";
        var position = code.IndexOf("List<", StringComparison.Ordinal) + "List<".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText is "for" or "return" or "class" or "var" or "void");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_Offer_PredefinedTypeKeywords_InsideGenericTypeArgument()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C { void M() { var a = new List<\n} }";
        var position = code.IndexOf("List<", StringComparison.Ordinal) + "List<".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "int");
        Assert.Contains(result!.Items, i => i.DisplayText == "string");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_Offer_UserDefinedType_InsideGenericTypeArgument()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class Foo { } class C { void M() { var a = new List<\n} }";
        var position = code.IndexOf("List<", StringComparison.Ordinal) + "List<".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Foo");
    }

    // Regression guard proving the assertions above actually exercise the fix rather than passing
    // vacuously: the exact same "myLocal" symbol, requested from an ordinary (non-generic-argument)
    // expression position two lines down, must still be offered — this is the general fallback path
    // the generic-argument narrowing must NOT apply everywhere, only inside a type argument list.
    [Fact]
    public async Task GetCompletionsAsync_Should_StillOffer_LocalVariable_OutsideGenericTypeArgument()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C { void M() { int myLocal = 1;\n myLocal\n} }";
        var position = code.LastIndexOf("myLocal", StringComparison.Ordinal) + "myLocal".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "myLocal");
    }
}
