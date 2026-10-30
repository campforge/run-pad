using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class PageTabsTests
{
    private static PageTabsFixture.Page P(string title) => new(title);

    [Fact]
    public void New_HasOneEmptyActiveTab()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();

        // Act + Assert
        Assert.Equal(1, sut.Count);
        Assert.Same(sut.All[0], sut.Active);
        Assert.Null(sut.Active.Current);
        Assert.False(sut.CanGoBack);
        Assert.False(sut.CanGoForward);
    }

    [Fact]
    public void Show_FirstPage_BecomesCurrentWithoutAnyHistory()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();

        // Act
        sut.Show(P("a"), PageTabsFixture.Unchanged);

        // Assert
        Assert.Equal(P("a"), sut.Active.Current);
        Assert.False(sut.CanGoBack);
    }

    [Fact]
    public void Show_SecondPage_PutsTheRememberedFirstOneOnTheBackStack()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);

        // Act
        sut.Show(P("b"), PageTabsFixture.Remember(42));

        // Assert
        Assert.Equal(P("b"), sut.Active.Current);
        Assert.Equal(new PageTabsFixture.Page("a", 42), sut.Active.Back.Peek());
    }

    [Fact]
    public void Show_AfterGoingBack_DropsTheForwardHistory()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);
        sut.Show(P("b"), PageTabsFixture.Unchanged);
        sut.GoBack(PageTabsFixture.Unchanged);
        Assert.True(sut.CanGoForward);

        // Act
        sut.Show(P("c"), PageTabsFixture.Unchanged);

        // Assert
        Assert.False(sut.CanGoForward);
    }

    [Fact]
    public void GoBack_AndForward_SwapTheCurrentPageWithTheTopOfTheOtherStack()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);
        sut.Show(P("b"), PageTabsFixture.Unchanged);

        // Act
        var back = sut.GoBack(PageTabsFixture.Remember(7));
        var forward = sut.GoForward(PageTabsFixture.Unchanged);

        // Assert
        Assert.Equal(P("a"), back);
        Assert.Equal(new PageTabsFixture.Page("b", 7), forward);
        Assert.Equal(forward, sut.Active.Current);
    }

    [Fact]
    public void GoBack_NothingToGoBackTo_ReturnsNullAndChangesNothing()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);

        // Act
        var back = sut.GoBack(PageTabsFixture.Unchanged);

        // Assert
        Assert.Null(back);
        Assert.Equal(P("a"), sut.Active.Current);
    }

    [Fact]
    public void GoBack_EmptyTab_ReturnsNull()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();

        // Act
        var back = sut.GoBack(PageTabsFixture.Unchanged);

        // Assert
        Assert.Null(back);
        Assert.Null(sut.GoForward(PageTabsFixture.Unchanged));
    }

    [Fact]
    public void InsertAfterActive_PutsTheNewTabRightOfTheActiveOneWithoutActivatingIt()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var first = sut.Active;
        var third = sut.InsertAfterActive();
        sut.Activate(first, PageTabsFixture.Unchanged);

        // Act
        var second = sut.InsertAfterActive();

        // Assert
        Assert.Equal([first, second, third], sut.All);
        Assert.Same(first, sut.Active);
    }

    [Fact]
    public void Activate_AnotherTab_RemembersThePageOfTheTabItLeaves()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);
        var left = sut.Active;
        var other = sut.InsertAfterActive();

        // Act
        var activated = sut.Activate(other, PageTabsFixture.Remember(11));

        // Assert
        Assert.True(activated);
        Assert.Same(other, sut.Active);
        Assert.Equal(new PageTabsFixture.Page("a", 11), left.Current);
    }

    [Fact]
    public void Activate_TabThatIsNotInTheList_DoesNothing()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var other = sut.InsertAfterActive();
        sut.Close(other);
        var active = sut.Active;

        // Act
        var activated = sut.Activate(other, PageTabsFixture.Unchanged);

        // Assert
        Assert.False(activated);
        Assert.Same(active, sut.Active);
    }

    [Fact]
    public void Close_TheLastTab_EmptiesItAndAsksToActivateItAgain()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);
        sut.Show(P("b"), PageTabsFixture.Unchanged);
        var tab = sut.Active;

        // Act
        var (found, toActivate) = sut.Close(tab);

        // Assert
        Assert.True(found);
        Assert.Same(tab, toActivate);
        Assert.Equal(1, sut.Count);
        Assert.Null(tab.Current);
        Assert.False(sut.CanGoBack);
    }

    [Fact]
    public void Close_TheActiveTab_HandsOverItsNeighbour()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var first = sut.Active;
        var second = sut.InsertAfterActive();
        var third = sut.InsertAfterActive(); // first, third, second
        sut.Activate(third, PageTabsFixture.Unchanged);

        // Act
        var (found, toActivate) = sut.Close(third);

        // Assert
        Assert.True(found);
        Assert.Same(second, toActivate); // the tab that took its place
        Assert.Equal([first, second], sut.All);
    }

    [Fact]
    public void Close_TheActiveTabAtTheEnd_HandsOverThePreviousOne()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var first = sut.Active;
        var second = sut.InsertAfterActive();
        sut.Activate(second, PageTabsFixture.Unchanged);

        // Act
        var (_, toActivate) = sut.Close(second);

        // Assert
        Assert.Same(first, toActivate);
    }

    [Fact]
    public void Close_AnotherTab_NeedsNothingToBeActivated()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var active = sut.Active;
        var other = sut.InsertAfterActive();

        // Act
        var (found, toActivate) = sut.Close(other);

        // Assert
        Assert.True(found);
        Assert.Null(toActivate);
        Assert.Same(active, sut.Active);
    }

    [Fact]
    public void Close_TabThatIsNotInTheList_IsNotFound()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        var other = sut.InsertAfterActive();
        sut.Close(other);

        // Act
        var (found, _) = sut.Close(other);

        // Assert
        Assert.False(found);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(-1, 2)]
    [InlineData(-4, 2)]
    public void Next_StepFromTheFirstOfThreeTabs_WrapsAround(int step, int expectedIndex)
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.InsertAfterActive();
        sut.InsertAfterActive();

        // Act
        var next = sut.Next(step);

        // Assert
        Assert.Same(sut.All[expectedIndex], next);
    }

    [Fact]
    public void Rewrite_ChangesEveryPageAndKeepsTheOrderOfTheStacks()
    {
        // Arrange
        var sut = new PageTabsFixture().CreateSut();
        sut.Show(P("a"), PageTabsFixture.Unchanged);
        sut.Show(P("b"), PageTabsFixture.Unchanged);
        sut.Show(P("c"), PageTabsFixture.Unchanged);
        sut.GoBack(PageTabsFixture.Unchanged); // current b, back a, forward c

        // Act
        sut.Rewrite(page => page with { Title = page.Title.ToUpperInvariant() });

        // Assert
        Assert.Equal("B", sut.Active.Current!.Title);
        Assert.Equal("A", sut.Active.Back.Peek().Title);
        Assert.Equal("C", sut.Active.Forward.Peek().Title);
        Assert.Equal("A", sut.GoBack(PageTabsFixture.Unchanged)!.Title);
    }
}
