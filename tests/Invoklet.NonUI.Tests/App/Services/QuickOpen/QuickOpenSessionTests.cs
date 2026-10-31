using Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;
using Invoklet.Services.QuickOpen;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen;

public sealed class QuickOpenSessionTests
{
    [Fact]
    public void New_Session_ListsTheRecentFileFirstAndSelectsIt()
    {
        // Arrange / Act
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Assert
        Assert.Equal(["Beta", "Alpha", "Gamma", "Sample One"], sut.Results.Select(e => e.Name));
        Assert.Equal("Beta", sut.Selected!.Name);
    }

    [Fact]
    public void SetQuery_TextThatMatchesSome_NarrowsTheListAndSelectsTheBest()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.Move(2);

        // Act
        sut.SetQuery("ga");

        // Assert
        Assert.Equal(["Gamma"], sut.Results.Select(e => e.Name));
        Assert.Equal(0, sut.SelectedIndex);
    }

    [Fact]
    public void SetQuery_TextThatMatchesNothing_SelectsNothing()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Act
        sut.SetQuery("zzz");

        // Assert
        Assert.Empty(sut.Results);
        Assert.Equal(-1, sut.SelectedIndex);
        Assert.Null(sut.Selected);
    }

    [Fact]
    public void Move_PastEitherEnd_StopsAtIt()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Act
        sut.Move(10);
        var last = sut.SelectedIndex;
        sut.Move(-10);

        // Assert
        Assert.Equal(3, last);
        Assert.Equal(0, sut.SelectedIndex);
    }

    [Fact]
    public void Move_NothingListed_DoesNothing()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.SetQuery("zzz");

        // Act
        sut.Move(1);

        // Assert
        Assert.Equal(-1, sut.SelectedIndex);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    [InlineData(4, 0)]
    public void Select_ARow_IsSelectedOnlyWhenItExists(int index, int expected)
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Act
        sut.Select(index);

        // Assert
        Assert.Equal(expected, sut.SelectedIndex);
    }

    [Fact]
    public void New_NoEntries_HasNothingSelected()
    {
        // Arrange / Act
        var sut = new Invoklet.Services.QuickOpen.QuickOpenSession([]);

        // Assert
        Assert.Equal(-1, sut.SelectedIndex);
        Assert.Null(sut.Selected);
    }

    [Fact]
    public void ShowScripts_Off_HidesTheScriptsAndKeepsTheSamples()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Act
        sut.ShowScripts = false;

        // Assert
        Assert.Equal(["Sample One"], sut.Results.Select(e => e.Name));
    }

    [Fact]
    public void ShowSamples_Off_HidesTheSamples()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();

        // Act
        sut.ShowSamples = false;

        // Assert
        Assert.DoesNotContain(sut.Results, e => e.Kind == QuickOpenKind.Sample);
    }

    [Fact]
    public void AddEntries_FilesOfOtherTypes_AreListedOnlyOnceIncluded()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.AddEntries([QuickOpenSessionFixture.Other]);
        var before = sut.Results.Count;

        // Act
        sut.IncludeOtherFiles = true;

        // Assert
        Assert.Equal(4, before);
        Assert.Contains(sut.Results, e => e.Kind == QuickOpenKind.OtherFile);
    }

    [Fact]
    public void SetContentMatches_SearchInsideOn_AppendsTheMatchesThatTheNamesMissed()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.SearchInside = true;
        sut.SetQuery("alpha");
        var byContent = QuickOpenSessionFixture.Entries[2];

        // Act
        sut.SetContentMatches("alpha", [byContent, QuickOpenSessionFixture.Entries[0]]);

        // Assert
        Assert.Equal(["Alpha", "Gamma"], sut.Results.Select(e => e.Name));
    }

    [Fact]
    public void SetContentMatches_SearchInsideOff_ShowsNoMatchOfTheContents()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.SetQuery("alpha");

        // Act
        sut.SetContentMatches("alpha", [QuickOpenSessionFixture.Entries[2]]);

        // Assert
        Assert.Equal(["Alpha"], sut.Results.Select(e => e.Name));
    }

    [Fact]
    public void SetContentMatches_ForATextThatHasChanged_IsIgnored()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.SearchInside = true;
        sut.SetQuery("alpha");
        sut.SetQuery("gamma");

        // Act
        sut.SetContentMatches("alpha", [QuickOpenSessionFixture.Entries[1]]);

        // Assert
        Assert.Equal(["Gamma"], sut.Results.Select(e => e.Name));
    }

    [Fact]
    public void ContentCandidates_AreTheEntriesTheFiltersLetThrough()
    {
        // Arrange
        var sut = new QuickOpenSessionFixture().CreateSut();
        sut.ShowSamples = false;

        // Act
        var candidates = sut.ContentCandidates;

        // Assert
        Assert.DoesNotContain(candidates, e => e.Kind == QuickOpenKind.Sample);
        Assert.Equal(3, candidates.Count);
    }
}
