using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "умное переименование" (smart rename, F2) — CSharpIntelliSenseService.
// GetRenameInfoAsync shares its "find the symbol under the caret and every occurrence" scan with
// GetDocumentHighlightsAsync (see CSharpIntelliSenseDocumentHighlightsTests.cs), only adding a
// filter for symbols the user's own script doesn't declare.
public class CSharpIntelliSenseRenameTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetRenameInfoAsync_Should_ReturnCurrentNameAndEveryOccurrence_ForALocalVariable()
    {
        using var service = CreateService();
        const string code = "var value = 1;\nvalue = value + 1;\nConsole.WriteLine(value);";
        var position = code.IndexOf("value + 1", StringComparison.Ordinal);

        var result = await service.GetRenameInfoAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("value", result!.CurrentName);
        Assert.Equal(4, result.Spans.Count);
        Assert.All(result.Spans, h => Assert.Equal("value", code.Substring(h.Start, h.Length)));
    }

    // A method/class/local the script itself declares must be renameable — the point of the
    // feature — even though it's also just "a symbol with a source location", same as the local
    // variable case above; this exercises a different declaration shape (method, not a local).
    [Fact]
    public async Task GetRenameInfoAsync_Should_ReturnInfo_ForAUserDeclaredMethod()
    {
        using var service = CreateService();
        const string code = "void Greet() { Console.WriteLine(\"hi\"); }\nGreet();";
        var position = code.IndexOf("Greet();", StringComparison.Ordinal);

        var result = await service.GetRenameInfoAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Greet", result!.CurrentName);
        Assert.Equal(2, result.Spans.Count); // the declaration + the one call
    }

    // The whole point of filtering by IsInSource: renaming call sites of a framework method
    // without also "renaming" the framework method itself would just break the script — Console.
    // WriteLine has no source location in the user's own document to rename AT.
    [Fact]
    public async Task GetRenameInfoAsync_Should_ReturnNull_ForAFrameworkSymbol()
    {
        using var service = CreateService();
        const string code = "Console.WriteLine(\"hi\");";
        var position = code.IndexOf("WriteLine", StringComparison.Ordinal);

        var result = await service.GetRenameInfoAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRenameInfoAsync_Should_ReturnNull_WhenCaretIsNotOnAnIdentifier()
    {
        using var service = CreateService();
        const string code = "var value = 1;";
        var position = code.IndexOf('=', StringComparison.Ordinal);

        var result = await service.GetRenameInfoAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
