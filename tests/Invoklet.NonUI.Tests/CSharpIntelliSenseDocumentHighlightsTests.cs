using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.NonUI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// CSharpIntelliSenseService.GetDocumentHighlightsAsync finds
// every occurrence of the symbol under the caret elsewhere in the same document. Roslyn ships a
// ready-made IDocumentHighlightsService for this, but it's internal to Microsoft.CodeAnalysis.
// Features (confirmed directly — see the method's own comment), so this is a from-scratch
// implementation using only public SemanticModel/SyntaxToken API; these tests are what actually
// pins its symbol-matching behavior down, not just "some Roslyn service handles it".
public class CSharpIntelliSenseDocumentHighlightsTests
{
    [Fact]
    public async Task GetDocumentHighlightsAsync_Should_FindEveryOccurrence_OfALocalVariable()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "var value = 1;\nvalue = value + 1;\nConsole.WriteLine(value);";
        var position = code.IndexOf("value + 1", StringComparison.Ordinal); // the second "value" on line 2

        var result = await service.GetDocumentHighlightsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // Declaration + "value = " + "value + 1" + "WriteLine(value)" = 4 occurrences.
        Assert.Equal(4, result!.Count);
        Assert.All(result, h => Assert.Equal("value", code.Substring(h.Start, h.Length)));
    }

    [Fact]
    public async Task GetDocumentHighlightsAsync_Should_ReturnNull_WhenCaretIsNotOnAnIdentifier()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "var value = 1;";
        var position = code.IndexOf('=', StringComparison.Ordinal);

        var result = await service.GetDocumentHighlightsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // Two different types' own same-named member must not cross-match — this is exactly why
    // GetDocumentHighlightsAsync compares SYMBOLS (SymbolEqualityComparer), not just identifier
    // text (which is only used as a cheap pre-filter before the semantic check).
    [Fact]
    public async Task GetDocumentHighlightsAsync_Should_NotMatchADifferentSymbolWithTheSameName()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class A { public int Value; }\nclass B { public int Value; }\n" +
            "var a = new A();\na.Value = 1;";
        var position = code.IndexOf("a.Value", StringComparison.Ordinal) + 2; // the "Value" in "a.Value"

        var result = await service.GetDocumentHighlightsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // A.Value's own declaration + the one "a.Value" usage — NOT B.Value's declaration.
        Assert.Equal(2, result!.Count);
    }
}
