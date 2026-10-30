using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.NonUI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// User-reported bug: `var a = new List<string>();` then `a` on its own line, then `.`
// replaced `a` with `args`. Traced to a genuine Roslyn quirk, confirmed directly against
// SemanticModel.LookupSymbols outside Invoklet entirely: a query position sitting at the EXACT end of
// the source text (no trailing newline/anything after it — precisely what "just typed a fresh
// single-letter identifier at the end of the last line" looks like) resolves against the bare
// CompilationUnit rather than the enclosing statement list, and silently stops finding block-scoped
// locals/parameters — leaving only symbols visible from an outer binder (like the implicit top-level-
// statement `args` parameter) as candidates. Not a ScopeRank/Priority tie-break at all: the local
// wasn't in the candidate list in the first place. Fixed by CSharpIntelliSenseService.TrailingPad — a
// single synthetic space appended once after the user's own code in the synced document, so no
// legitimate query position (always <= the user's own code length) ever coincides with the document's
// new true end again.
public class CSharpIntelliSenseEndOfBufferTests
{
    [Fact]
    public async Task GetCompletionsAsync_Should_FindTheLocalVariable_AtTheVeryEndOfTheBuffer()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        // No trailing newline after the second "a" — the caret sits at the literal end of the text,
        // exactly like right after typing a fresh single-letter identifier with nothing typed after.
        const string code = "var a = new System.Collections.Generic.List<string>();\na";

        var result = await service.GetCompletionsAsync(code, code.Length,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "a" && i.Kind == Invoklet.Contracts.IntelliSense.CompletionItemKind.Variable);
    }

    // Regression guard proving the assertion above actually exercises the fix: the exact same local
    // variable, requested one character earlier (position code.Length - 1, i.e. before the caret sits
    // at the true end) already worked correctly even before this fix — the bug is specifically about
    // the position landing exactly at the end of the text, not about resolving "a" in general.
    [Fact]
    public async Task GetCompletionsAsync_Should_FindTheLocalVariable_OneCharacterBeforeTheEnd()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "var a = new System.Collections.Generic.List<string>();\na\n";

        var result = await service.GetCompletionsAsync(code, code.Length - 1,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "a");
    }
}
