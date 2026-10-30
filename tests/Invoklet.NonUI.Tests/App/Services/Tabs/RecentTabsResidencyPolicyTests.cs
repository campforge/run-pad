using Invoklet.NonUI.Tests.App.Services.Tabs.Fixtures;
using Invoklet.Services.Tabs;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Tabs;

public sealed class RecentTabsResidencyPolicyTests
{
    private static void UseInOrder(RecentTabsResidencyPolicy sut, IEnumerable<RecentTabsResidencyPolicyFixture.Tab> tabs)
    {
        foreach (var tab in tabs) sut.Touch(tab);
    }

    [Fact]
    public void ColdTabs_MoreTabsThanTheLimit_ReturnsAllButTheThreeUsedLast()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(6);
        UseInOrder(sut, tabs);

        // Act
        var cold = sut.ColdTabs(tabs, _ => true);

        // Assert
        Assert.Equal(3, cold.Count);
        Assert.All(tabs.Take(3), tab => Assert.Contains(tab, cold));
    }

    [Fact]
    public void ColdTabs_NotMoreTabsThanTheLimit_ReturnsNone()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(3);
        UseInOrder(sut, tabs);

        // Act
        var cold = sut.ColdTabs(tabs, _ => true);

        // Assert
        Assert.Empty(cold);
    }

    [Fact]
    public void ColdTabs_CustomLimit_KeepsThatManyWarm()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut(warmTabs: 1);
        var tabs = fixture.CreateTabs(3);
        UseInOrder(sut, tabs);

        // Act
        var cold = sut.ColdTabs(tabs, _ => true);

        // Assert
        Assert.Equal(2, cold.Count);
        Assert.DoesNotContain(tabs[2], cold);
    }

    [Fact]
    public void ColdTabs_ATabWasSelectedAgain_PushesOutTheOldestWarmOne()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(5);
        UseInOrder(sut, tabs); // tab1 and tab2 are cold
        sut.Touch(tabs[0]);

        // Act
        var cold = sut.ColdTabs(tabs, _ => true);

        // Assert
        Assert.DoesNotContain(tabs[0], cold);
        Assert.Contains(tabs[1], cold);
        Assert.Contains(tabs[2], cold); // tab3 became the oldest of the warm ones
    }

    [Fact]
    public void ColdTabs_ATabCannotBeUnloaded_StaysOutWithoutKeepingAnotherOneWarm()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(5);
        UseInOrder(sut, tabs);

        // Act
        var cold = sut.ColdTabs(tabs, tab => tab != tabs[0]);

        // Assert
        Assert.Equal([tabs[1]], cold);
    }

    [Fact]
    public void ColdTabs_ATabNeverUsed_CountsAsTheOldest()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(3);
        var unknown = new RecentTabsResidencyPolicyFixture.Tab("never touched");
        UseInOrder(sut, tabs);

        // Act
        var cold = sut.ColdTabs(tabs.Append(unknown), _ => true);

        // Assert
        Assert.Equal([unknown], cold);
    }

    [Fact]
    public void ColdTabs_AForgottenTab_IsUnknownAgainAndSoTheOldest()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();
        var sut = fixture.CreateSut();
        var tabs = fixture.CreateTabs(4);
        UseInOrder(sut, tabs);
        sut.Forget(tabs[3]);

        // Act
        var cold = sut.ColdTabs(tabs, _ => true);

        // Assert
        Assert.Equal([tabs[3]], cold);
    }

    [Fact]
    public void Constructor_NoTabMayStayWarm_Throws()
    {
        // Arrange
        var fixture = new RecentTabsResidencyPolicyFixture();

        // Act
        var create = () => fixture.CreateSut(warmTabs: 0);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(create);
    }
}
