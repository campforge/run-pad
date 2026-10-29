using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Live user report: `var c = new Tests() { P1 = "1", ... };` never offered Tests' own
// remaining settable members inside the `{ }` — every completion branch checked for a dot/member
// access or fell through to whatever's visible in the enclosing scope, nothing ever special-cased an
// object-initializer position.
public class CSharpIntelliSenseObjectInitializerCompletionTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    private const string ClassDeclaration =
        "public class Tests { public string P1 = \"\"; public string P2 = \"\"; public string P3 { get; set; } = \"\"; " +
        "public const string ConstField = \"c\"; public static string StaticField = \"s\"; public readonly string ReadonlyField = \"r\"; " +
        "public string ReadonlyProp { get; } = \"p\"; }\n";

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferRemainingSettableMembers_InsideAnObjectInitializer()
    {
        using var service = CreateService();
        var code = ClassDeclaration + "var c = new Tests() { P1 = \"1\", };";
        var position = code.IndexOf("};", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var names = result!.Items.Select(i => i.DisplayText).ToList();
        Assert.Contains("P2", names);
        Assert.Contains("P3", names);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferAMember_AlreadyAssigned_InsideAnObjectInitializer()
    {
        using var service = CreateService();
        var code = ClassDeclaration + "var c = new Tests() { P1 = \"1\", };";
        var position = code.IndexOf("};", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == "P1");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferConstOrStaticOrReadonlyMembers_InsideAnObjectInitializer()
    {
        using var service = CreateService();
        var code = ClassDeclaration + "var c = new Tests() { P1 = \"1\", };";
        var position = code.IndexOf("};", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var names = result!.Items.Select(i => i.DisplayText).ToList();
        Assert.DoesNotContain("ConstField", names);
        Assert.DoesNotContain("StaticField", names);
        Assert.DoesNotContain("ReadonlyField", names);
        Assert.DoesNotContain("ReadonlyProp", names);
    }

    // Right after the opening brace, nothing assigned yet — every settable member should be offered.
    [Fact]
    public async Task GetCompletionsAsync_Should_OfferEverySettableMember_RightAfterTheOpeningBrace()
    {
        using var service = CreateService();
        var code = ClassDeclaration + "var c = new Tests() {  };";
        var position = code.IndexOf("{  };", StringComparison.Ordinal) + 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var names = result!.Items.Select(i => i.DisplayText).ToList();
        Assert.Contains("P1", names);
        Assert.Contains("P2", names);
        Assert.Contains("P3", names);
    }

    // Confirms the enclosing-scope fallback (locals, keywords, ...) is genuinely suppressed here, not
    // just that the settable members happen to also be present alongside it.
    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferEnclosingScopeSymbolsOrKeywords_InsideAnObjectInitializer()
    {
        using var service = CreateService();
        var code = "var someLocal = 1;\n" + ClassDeclaration + "var c = new Tests() { P1 = \"1\", };";
        var position = code.IndexOf("};", StringComparison.Ordinal);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var names = result!.Items.Select(i => i.DisplayText).ToList();
        Assert.DoesNotContain("someLocal", names);
        Assert.DoesNotContain("readonly", names);
    }

    // A plain new-tab expression with nothing to complete (a type with no settable members at all)
    // should still suppress the generic fallback rather than showing unrelated scope noise.
    [Fact]
    public async Task GetCompletionsAsync_Should_ReturnNothing_WhenTheTypeHasNoSettableMembersLeft()
    {
        using var service = CreateService();
        const string emptyClass = "public class Empty { public Empty() {} }\n";
        var code = emptyClass + "var c = new Empty() {  };";
        var position = code.IndexOf("{  };", StringComparison.Ordinal) + 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
