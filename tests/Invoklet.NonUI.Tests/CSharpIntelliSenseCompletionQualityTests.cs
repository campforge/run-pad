using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// User feedback after trying 24/7 auto-invoke live:
// 1. Completion/signature-help/quick-info showed up while typing prose inside a comment.
// 2. Scope ordering was pure alphabetical — a nearby local could sort behind an unrelated,
//    merely-imported type or a keyword.
public class CSharpIntelliSenseCompletionQualityTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhenTypingInsideALineComment()
    {
        using var service = CreateService();
        const string code = "var x = 1;\n// just some pro";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhenTypingInsideABlockComment()
    {
        using var service = CreateService();
        const string code = "var x = 1;\n/* still writing thi";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSignatureHelpAsync_Should_ReturnNothing_WhenTheOpenParenIsInsideAComment()
    {
        using var service = CreateService();
        const string code = "var x = 1;\n// looks like a call Foo(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_RankLocalsAndParameters_BeforeTypesAndKeywords()
    {
        using var service = CreateService();
        // "Random" is a real BCL type and "record"/"readonly" are keywords — both would otherwise
        // sort ahead of "r" alphabetically-close candidates; "r" itself is a local the user just
        // declared, and should rank first regardless of alphabetical position.
        const string code = "var r = 1;\nr;";
        var position = code.Length - 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var names = result!.Items.Select(i => i.DisplayText).ToList();
        var localIndex = names.IndexOf("r");
        var keywordIndex = names.IndexOf("readonly");
        Assert.True(localIndex >= 0, "expected the local 'r' to be offered");
        Assert.True(keywordIndex >= 0, "expected the 'readonly' keyword to be offered");
        Assert.True(localIndex < keywordIndex, "expected the in-scope local to rank before a same-ish-named keyword");
    }

    // The editor used to hardcode Priority (Kind == Method ? 1.0 : 0.9) instead of
    // taking it from the language model — AvaloniaEdit's CompletionList only consults Priority as a
    // tie-break among items of otherwise-equal match quality, so a method used to win pre-selection
    // ties against an equally-good-matching local/parameter despite ranking behind it in the list.
    [Fact]
    public async Task GetCompletionsAsync_Should_GiveLocalsAndParameters_HigherPriority_ThanMembersAndKeywords()
    {
        using var service = CreateService();
        const string code = "var r = 1;\nr;";
        var position = code.Length - 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var local = result!.Items.Single(i => i.DisplayText == "r");
        var keyword = result.Items.Single(i => i.DisplayText == "readonly");
        Assert.True(local.Priority > keyword.Priority,
            $"expected local 'r' (Priority={local.Priority}) to outrank keyword 'readonly' (Priority={keyword.Priority})");
    }

    // Completion showed up inside string literals — the
    // sixteenth pass's IsInsideComment guard only covers lexical trivia (// and /* */), but string
    // content is an ordinary token, not trivia, so it needed its own check.
    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhenTypingInsideAStringLiteral()
    {
        using var service = CreateService();
        const string code = "var s = \"hello wor\";";
        var position = code.IndexOf("wor", StringComparison.Ordinal) + 3;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // "s?." resolved to nothing — the dot-completion branch only recognized plain
    // MemberAccessExpressionSyntax, not the MemberBindingExpressionSyntax a '?.' produces.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferMembers_AfterConditionalAccess()
    {
        using var service = CreateService();
        const string code = "string? s = \"x\";\ns?.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Length");
    }

    // "System." resolved to nothing — the qualifier names a namespace, which has no
    // GetTypeInfo() result (only GetSymbolInfo does), so it fell through to "nothing to complete".
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferNestedNamespacesAndTypes_AfterANamespaceDot()
    {
        using var service = CreateService();
        const string code = "System.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Collections");
        Assert.Contains(result.Items, i => i.DisplayText == "String");
    }

    // ToItem had no case for INamespaceSymbol, so "System" never appeared in the
    // general (non-dot) completion list even though LookupSymbols(pos) does return it as a candidate.
    [Fact]
    public async Task GetCompletionsAsync_Should_IncludeNamespaces_InTheGeneralList()
    {
        using var service = CreateService();
        const string code = "Sys";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "System");
    }

    // Local functions (MethodKind.LocalFunction) were filtered out of ToItem's
    // IMethodSymbol case — the primary way top-level-statement scripts structure code, so this was
    // the single most common non-trivial identifier that never completed.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferLocalFunctions()
    {
        using var service = CreateService();
        const string code = "void Foo() { }\nFo;";
        var position = code.Length - 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Foo");
    }

    // LookupSymbols(container: type) returns every member regardless of static-ness
    // — "d." (an instance of DateTime) used to also offer the static Parse method.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferOnlyInstanceMembers_ForAValueQualifier()
    {
        using var service = CreateService();
        const string code = "var d = DateTime.Now;\nd.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "AddDays");
        Assert.DoesNotContain(result.Items, i => i.DisplayText == "Parse");
    }

    // The same mixing in the other direction — "DateTime." (the type itself) used to
    // also offer instance members like AddDays, which aren't callable through the type name.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferOnlyStaticMembers_ForATypeQualifier()
    {
        using var service = CreateService();
        const string code = "DateTime.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Now");
        Assert.DoesNotContain(result.Items, i => i.DisplayText == "AddDays");
    }

    // Signature help only recognized InvocationExpressionSyntax — "new Foo(" (an
    // ObjectCreationExpressionSyntax) never showed constructor parameters at all.
    [Fact]
    public async Task GetSignatureHelpAsync_Should_ShowConstructorOverloads_ForAnObjectCreationExpression()
    {
        using var service = CreateService();
        const string code = "var sb = new System.Text.StringBuilder(";

        var result = await service.GetSignatureHelpAsync(code, code.Length, '(', TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.Items.Count > 0, "expected at least one StringBuilder constructor overload");
    }

    // Delegate types (e.g. System.Action) fell into ToItem's default branch and
    // showed up with the same icon as an ordinary class, indistinguishable in the completion list.
    [Fact]
    public async Task GetCompletionsAsync_Should_ClassifyDelegateTypes_Distinctly_NotAsClass()
    {
        using var service = CreateService();
        const string code = "int x = 1;";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var action = result!.Items.Single(i => i.DisplayText == "Action");
        Assert.Equal(CompletionItemKind.Delegate, action.Kind);
    }

    // Type parameters (the `T` in a generic method's own signature/body) were
    // dropped entirely by ToItem — inside the method they're a real, referenceable symbol, not just
    // declaration-site syntax.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferTypeParameters_InsideAGenericLocalFunctionBody()
    {
        using var service = CreateService();
        const string code = "T Identity<T>(T v)\n{\n    return v;\n}";
        var position = code.IndexOf("return", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var typeParam = result!.Items.Single(i => i.DisplayText == "T");
        Assert.Equal(CompletionItemKind.TypeParameter, typeParam.Kind);
    }
}
