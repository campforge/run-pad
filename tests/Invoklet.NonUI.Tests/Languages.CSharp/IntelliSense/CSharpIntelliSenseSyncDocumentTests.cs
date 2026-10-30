using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Invoklet.NonUI.Tests;

// SyncDocument now diffs the
// previous full document text against the new one (ComputeMinimalChange) instead of always handing
// Roslyn a brand-new SourceText.From(...), so SourceText.WithChanges can preserve lineage and let the
// incremental lexer/parser reuse syntax nodes outside the edited span. These tests exercise the diff
// math directly (see ComputeMinimalChange's own doc comment for why a common-prefix/suffix span is
// enough for this call pattern) rather than only indirectly through a live SyncDocument call, since a
// wrong span here would silently corrupt every completion/diagnostic built on top of it.
public class CSharpIntelliSenseSyncDocumentTests
{
    private static string Apply(string oldText, TextChange change) =>
        SourceText.From(oldText).WithChanges(change).ToString();

    [Fact]
    public void ComputeMinimalChange_Should_ReturnEmptyReplacement_ForIdenticalText()
    {
        const string text = "class C { void M() { } }";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(text, text);

        Assert.Equal(0, change.Span.Length);
        Assert.Equal("", change.NewText);
        Assert.Equal(text, Apply(text, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_CoverOnlyTheAppendedTail_ForATypedCharacter()
    {
        const string oldText = "var x = 1";
        const string newText = "var x = 12";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(new TextSpan(oldText.Length, 0), change.Span);
        Assert.Equal("2", change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_CoverOnlyTheDeletedChar_ForABackspace()
    {
        const string oldText = "var x = 12";
        const string newText = "var x = 1";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(new TextSpan(newText.Length, 1), change.Span);
        Assert.Equal("", change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_CoverOnlyTheEditedMiddle_ForAnInsertBetweenSharedPrefixAndSuffix()
    {
        const string oldText = "var name = \"AC\";";
        const string newText = "var name = \"ABC\";";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        // Shared prefix `var name = "A`, shared suffix `C";` — only the inserted `B` should be covered.
        Assert.Equal(new TextSpan(13, 0), change.Span);
        Assert.Equal("B", change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_ReplaceTheWholeSpan_WhenTextsShareNoPrefixOrSuffix()
    {
        const string oldText = "abc";
        const string newText = "xyz";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(new TextSpan(0, 3), change.Span);
        Assert.Equal("xyz", change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_CoverTheWholeOldText_WhenNewTextIsEmpty()
    {
        const string oldText = "class C { }";
        const string newText = "";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(new TextSpan(0, oldText.Length), change.Span);
        Assert.Equal("", change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    [Fact]
    public void ComputeMinimalChange_Should_InsertAtStart_WhenOldTextIsEmpty()
    {
        const string oldText = "";
        const string newText = "class C { }";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(new TextSpan(0, 0), change.Span);
        Assert.Equal(newText, change.NewText);
        Assert.Equal(newText, Apply(oldText, change));
    }

    // A repeated character straddling the naive common-prefix/suffix boundary (e.g. typing a second
    // "a" right after the first one in "a" -> "aa") is the case most likely to trip up an off-by-one
    // in the prefix/suffix scan — the shared prefix and shared suffix scans can overlap on a run of
    // identical characters, so this pins down that Apply still reproduces newText exactly.
    [Fact]
    public void ComputeMinimalChange_Should_ApplyCorrectly_WhenPrefixAndSuffixScansOverlapOnARepeatedChar()
    {
        const string oldText = "a";
        const string newText = "aa";

        var change = CSharpIntelliSenseService.ComputeMinimalChange(oldText, newText);

        Assert.Equal(newText, Apply(oldText, change));
    }

    // Confirms SyncDocument's own branching (not just ComputeMinimalChange in isolation) actually
    // takes the incremental WithChanges path on the second and later calls: when two SourceText
    // instances share a lineage, GetChangeRanges reports only the actually-edited span; for two
    // unrelated instances (e.g. two independent SourceText.From calls with no WithChanges lineage
    // between them) Roslyn has no way to localize the difference and reports the whole new text as
    // one changed range instead. Verified via revert-and-fail: temporarily forcing SyncDocument to
    // always take the SourceText.From branch (never WithChanges) made this test fail with a
    // whole-document range (NewLength 173, not 1), then the fix was reverted back once confirmed.
    [Fact]
    public async Task SyncDocument_Should_PreserveSourceTextLineage_AcrossConsecutiveSyncs()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();

        await service.GetCompletionsAsync("var x = 1", 9, ct: TestContext.Current.CancellationToken);
        var afterFirst = service.LastSyncedSourceTextForTests;
        Assert.NotNull(afterFirst);

        await service.GetCompletionsAsync("var x = 12", 10, ct: TestContext.Current.CancellationToken);
        var afterSecond = service.LastSyncedSourceTextForTests;
        Assert.NotNull(afterSecond);

        var ranges = afterSecond!.GetChangeRanges(afterFirst!).ToList();
        var range = Assert.Single(ranges);
        Assert.Equal(1, range.NewLength);
    }
}
