using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #59 remainder: contextual keywords like "orderby"/"equals"/"get"/"value" were
// offered in completion EVERYWHERE, not just where they're syntactically legal — e.g. "orderby"
// suggested inside an ordinary method body, nowhere near a LINQ query. Fixed via two syntax-position
// checks (IsInsideQueryExpression/IsInsideAccessorContext), same double-boundary-check pattern as
// IsInsideComment/IsDeclarationNamePosition.
public class CSharpIntelliSenseContextualKeywordTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Theory]
    [InlineData("orderby")]
    [InlineData("equals")]
    [InlineData("ascending")]
    [InlineData("into")]
    [InlineData("join")]
    public async Task GetCompletionsAsync_Should_NotOffer_QueryContinuationKeywords_OutsideAQueryExpression(string keyword)
    {
        using var service = CreateService();
        const string code = "int x = 1;\nx";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == keyword);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_Offer_OrderbyInto_InsideARealQueryExpression()
    {
        using var service = CreateService();
        // Cursor positioned right at the "select" keyword itself (a real, already-parsed token) rather
        // than at trailing whitespace at end-of-document — an unclosed/incomplete query at EOF resolves
        // through Roslyn's missing-token recovery in ways too implementation-specific to rely on here.
        const string code = "var q = from n in new[] { 1, 2, 3 }\n         select n;";
        var position = code.IndexOf("select", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "orderby");
        Assert.Contains(result!.Items, i => i.DisplayText == "select");
        Assert.Contains(result!.Items, i => i.DisplayText == "where");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("value")]
    public async Task GetCompletionsAsync_Should_NotOffer_AccessorKeywords_OutsideAnAccessorList(string keyword)
    {
        using var service = CreateService();
        const string code = "void Main()\n{\n    ";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == keyword);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_Offer_GetSet_InsideAnEmptyAccessorList()
    {
        using var service = CreateService();
        const string code = "class Foo\n{\n    public int Bar { ";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "get");
        Assert.Contains(result!.Items, i => i.DisplayText == "set");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_Offer_Value_InsideASetterBody()
    {
        using var service = CreateService();
        const string code = "class Foo\n{\n    private int _bar;\n    public int Bar { set { _bar = ";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "value");
    }

    // Regression guard: "where" is deliberately excluded from the query-continuation filter (it's
    // also the unrelated generic type-constraint keyword) — it must keep showing everywhere, as before.
    [Fact]
    public async Task GetCompletionsAsync_Should_StillOffer_Where_OutsideAQueryExpression()
    {
        using var service = CreateService();
        const string code = "int x = 1;\nx";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "where");
    }
}
