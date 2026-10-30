using Invoklet.Services.QuickOpen;
using Xunit;
using static Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures.QuickOpenMatcherFixture;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen;

public sealed class QuickOpenMatcherTests
{
    [Fact]
    public void Rank_NothingTyped_ListsRecentFilesFirstMostRecentFirstThenTheRestByName()
    {
        // Arrange
        var entries = new[] { Entry("Zeta"), Entry("Alpha"), Entry("Older", recentRank: 3), Entry("Newest", recentRank: 0) };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "  ");

        // Assert
        Assert.Equal(["Newest", "Older", "Alpha", "Zeta"], Names(ranked));
    }

    [Fact]
    public void Rank_TheKindOfMatch_OrdersExactThenPrefixThenWordStartsThenPieceThenLetters()
    {
        // Arrange
        var entries = new[]
        {
            Entry("big_foo_bar"),   // a piece
            Entry("FooBar"),        // a prefix
            Entry("foo"),           // exact
            Entry("FancyOrOther"),  // starts of words F, O, O
            Entry("fxoxo"),         // the letters in order
            Entry("unrelated"),
        };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "foo");

        // Assert
        Assert.Equal(["foo", "FooBar", "FancyOrOther", "big_foo_bar", "fxoxo"], Names(ranked));
    }

    [Fact]
    public void Rank_LettersOfWordStarts_FindMyFirstReportInEitherSpelling()
    {
        // Arrange
        var entries = new[] { Entry("MyFirstReport"), Entry("my_first_report"), Entry("Unrelated") };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "mfr");

        // Assert
        Assert.Equal(["MyFirstReport", "my_first_report"], Names(ranked).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rank_CaseOfTheQuery_DoesNotMatter()
    {
        // Arrange
        var entries = new[] { Entry("Invoice") };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "INVO");

        // Assert
        Assert.Equal(["Invoice"], Names(ranked));
    }

    [Fact]
    public void Rank_NameAbsentButFolderMatches_OffersTheFileAfterNameMatches()
    {
        // Arrange
        var entries = new[] { Entry("Report", folder: "sales"), Entry("sales-notes") };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "sales");

        // Assert
        Assert.Equal(["sales-notes", "Report"], Names(ranked));
    }

    [Fact]
    public void Rank_NothingMatches_IsEmpty()
    {
        // Arrange
        var entries = new[] { Entry("Alpha"), Entry("Beta") };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "zzz");

        // Assert
        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_TwoMatchesOfTheSameKind_PutsTheRecentOneFirst()
    {
        // Arrange
        var entries = new[] { Entry("ReportB"), Entry("ReportA", recentRank: 5) };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "report");

        // Assert
        Assert.Equal(["ReportA", "ReportB"], Names(ranked));
    }

    [Fact]
    public void Rank_ARecentFileWithAWeakerMatch_DoesNotPassABetterMatch()
    {
        // Arrange
        var entries = new[] { Entry("rxexp", recentRank: 0), Entry("Report") };

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "rep");

        // Assert
        Assert.Equal(["Report", "rxexp"], Names(ranked));
    }

    [Fact]
    public void Rank_MoreMatchesThanTheLimit_KeepsTheBestOnes()
    {
        // Arrange
        var entries = Enumerable.Range(0, 20).Select(i => Entry($"Report{i:00}")).ToArray();

        // Act
        var ranked = QuickOpenMatcher.Rank(entries, "report", limit: 5);

        // Assert
        Assert.Equal(["Report00", "Report01", "Report02", "Report03", "Report04"], Names(ranked));
    }
}
