using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ObjectExtensions.Dump is deliberately many small overloads (Dump(), Dump(label), Dump(depth),
// Dump(label, depth), Dump(DumpOptions)) instead of one method with a pile of nullable parameters —
// see ObjectExtensions.cs. This guards that design choice at the signature-help level: whatever
// overload CSharpIntelliSenseService's custom signature-help resolves to for "x.Dump(" should never
// balloon back into a many-parameter wall.
public class CSharpIntelliSenseSignatureHelpTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task SignatureHelp_ForDump_Should_NeverShowAManyParameterOverload()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.All(result!.Items, item => Assert.True(item.Parameters.Count <= 3,
            $"Expected only small Dump overloads, but got: {string.Concat(item.Signature.Select(s => s.Text))}"));
    }

    [Fact]
    public async Task Completions_Should_OfferDump()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Du";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Dump");
    }

    // TODO.md #65: EditorTabItemControl uses IsArgumentSlotEmpty to redirect an explicit Ctrl+Space
    // request to overloads instead of a completion list, right after "(" — this is the underlying
    // signal that decision is based on.
    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotEmpty_RightAfterOpenParen()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsArgumentSlotEmpty);
    }

    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotNotEmpty_WhenPartiallyTyped()
    {
        using var service = CreateService();
        // "x" here is a partially-typed identifier argument (not a string literal — signature help
        // deliberately returns null while inside a string/char literal, a separate, pre-existing
        // check unrelated to this test), same shape as e.g. typing a variable name into the call.
        const string code = "var x = 42;\nx.Dump(x";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsArgumentSlotEmpty);
    }

    // Right after a "," with nothing typed for the next argument yet — same "fresh slot" as right
    // after "(", not just the first argument.
    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotEmpty_RightAfterComma()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(\"label\", ";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsArgumentSlotEmpty);
    }
}
