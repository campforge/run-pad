using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.NonUI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Completion now offers types from an added-but-not-yet-imported NuGet
// package too (ReSharper-style), tagged with the namespace EditorTabItemControl should add as a
// `using` if the item is accepted (see EditorCompletionDataTests for the generic accept-side
// plumbing). Framework types are offered too, once three letters are typed.
public class CSharpIntelliSenseAutoImportTests
{
    private static CSharpIntelliSenseService CreateServiceWithNewtonsoftJson()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        var service = new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdatePackageReferences(new Dictionary<string, Package> { ["Newtonsoft.Json"] = new("13.0.3") });
        return service;
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferAnUnimportedType_FromAnAddedNuGetPackage()
    {
        using var service = CreateServiceWithNewtonsoftJson();
        const string code = "JsonConv";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var item = result!.Items.Single(i => i.DisplayText == "JsonConvert");
        Assert.Contains("Newtonsoft.Json", item.UsingsToAdd);
        // Item 60 point 6 — the list row itself must carry the "(in Namespace)" annotation, not just
        // the accept-time using-add side effect, so the user can tell before accepting.
        Assert.Equal("Newtonsoft.Json", item.Detail);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_SortAnUnimportedType_AfterAnOrdinaryVisibleType()
    {
        using var service = CreateServiceWithNewtonsoftJson();
        // A type declared right in the script ("Zebra", ScopeRank 2, genuinely visible/in-scope, not
        // just a local) is offered alongside the unimported "JsonConvert" (ScopeRank 3) once "JsonConv"
        // is typed — LookupSymbols returns every visible symbol regardless of what's typed so far, only
        // the importable-type search is prefix-filtered. "Zebra" sorts alphabetically AFTER
        // "JsonConvert" on purpose: item 60 point 6 requires visible types before importable ones, so if
        // both shared one rank tier, alphabetical order would put the package type first — the wrong
        // way round. Only the separate, lower-tier rank for importable types (3 vs 2) makes "Zebra" win.
        const string code = "class Zebra { }\nJsonConv;";
        var position = code.Length - 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var items = result!.Items.ToList();
        var visibleIndex = items.FindIndex(i => i.DisplayText == "Zebra");
        var importableIndex = items.FindIndex(i => i.DisplayText == "JsonConvert");
        Assert.True(visibleIndex >= 0 && importableIndex >= 0 && visibleIndex < importableIndex);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferUnimportedTypes_WhenNothingIsTypedYet()
    {
        using var service = CreateServiceWithNewtonsoftJson();
        // A bare explicit invoke with no identifier prefix at all — GetIdentifierPrefix returns "".
        const string code = "int x = 1;\n";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.UsingsToAdd.Count > 0);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferAnUnimportedType_WhenTheNameAlreadyResolvesInScope()
    {
        using var service = CreateServiceWithNewtonsoftJson();
        // A local named "JsonConvert" already occupies that name — offering the package type too
        // would be an ambiguous, possibly-wrong suggestion, so it must be excluded entirely rather
        // than offered alongside (or instead of) the local.
        const string code = "int JsonConvert = 1;\nJsonConv;";
        var position = code.Length - 1;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var item = result!.Items.Single(i => i.DisplayText == "JsonConvert");
        Assert.Empty(item.UsingsToAdd);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferUnimportedTypes_WhenNoPackagesAreAdded()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        using var service = new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
        const string code = "JsonConv";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        // Without a package reference, "JsonConvert" simply isn't reachable at all.
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == "JsonConvert");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferAnUnimportedFrameworkType()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "StringBuil";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        var item = Assert.Single(result!.Items, i => i.DisplayText == "StringBuilder");
        Assert.Equal(["System.Text"], item.UsingsToAdd);
        Assert.Equal("System.Text", item.Detail);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferUnimportedFrameworkTypes_ForOneOrTwoLetters()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "St";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.UsingsToAdd.Count > 0);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferEachTypeOfTheSameName_WithItsNamespace()
    {
        using var service = CreateServiceWithNewtonsoftJson();
        const string code = "JsonSeriali";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        var namespaces = result!.Items.Where(i => i.DisplayText == "JsonSerializer").Select(i => i.Detail).Order().ToList();
        Assert.Equal(["Newtonsoft.Json", "System.Text.Json"], namespaces);
    }
}
