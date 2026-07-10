using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ObjectExtensions.Dump's rich/extended overload (11 parameters — maxRows, collapseTo, noTotals,
// ...) is marked [EditorBrowsable(EditorBrowsableState.Never)] specifically because it's unreadable
// in a signature-help popup — see ObjectExtensions.cs. CSharpIntelliSenseService is the one place
// that has to actually honor that attribute (it builds completion/signature help from raw Roslyn
// symbols, not the stock Roslyn CompletionService, so nothing did this before).
public class CSharpIntelliSenseEditorBrowsableTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task SignatureHelp_Should_HideDumpsRichOverload_ButShowDumpDepthOverload()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // The hidden overload has 11 parameters (label, maxDepth, maxRows, ...) — none of the
        // visible ones should come anywhere close.
        Assert.All(result!.Items, item => Assert.True(item.Parameters.Count <= 3,
            $"Expected only LINQPad-sized Dump overloads, but got: {item.DisplaySignature}"));
        Assert.DoesNotContain(result.Items, item => item.Parameters.Any(p => p.Name == "maxRows"));
        Assert.Contains(result.Items, item => item.Parameters.Any(p => p.Name == "depth"));
    }

    [Fact]
    public async Task Completions_Should_StillOfferDump_AfterHidingRichOverload()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Du";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Dump");
    }

    [Fact]
    public async Task Completions_DumpItemDescription_Should_NeverShowTheHiddenRichOverload()
    {
        // All Dump overloads collapse into a single "Dump" completion item (grouped by name) — its
        // hover Description picks one overload's signature as a representative. Filtering hidden
        // symbols out before that grouping (rather than after) guarantees the hidden 11-parameter
        // overload can never be the one picked, regardless of enumeration order.
        using var service = CreateService();
        const string code = "var x = 42;\nx.Du";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        var dumpItem = Assert.Single(result!.Items, i => i.DisplayText == "Dump");
        Assert.DoesNotContain("maxRows", dumpItem.Description);
    }
}
