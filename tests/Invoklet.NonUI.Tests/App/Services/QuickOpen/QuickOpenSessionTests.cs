using Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;
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
        Assert.Equal(["Beta", "Alpha", "Gamma"], sut.Results.Select(e => e.Name));
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
        Assert.Equal(2, last);
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
    [InlineData(3, 0)]
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
}
