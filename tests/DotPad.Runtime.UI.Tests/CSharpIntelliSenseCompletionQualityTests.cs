using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #51 follow-up (user feedback after trying 24/7 auto-invoke live):
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
}
