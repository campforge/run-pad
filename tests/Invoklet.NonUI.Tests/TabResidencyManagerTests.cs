using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests;

public class TabResidencyManagerTests
{
    private sealed class Tab(string name)
    {
        public override string ToString() => name;
    }

    private static List<Tab> Open(TabResidencyManager manager, int count)
    {
        var tabs = Enumerable.Range(1, count).Select(i => new Tab($"tab{i}")).ToList();
        foreach (var tab in tabs) manager.Touch(tab);
        return tabs;
    }

    [Fact]
    public void ColdTabs_Should_BeEveryTabButTheThreeUsedLast()
    {
        var manager = new TabResidencyManager();
        var tabs = Open(manager, 6);

        var cold = manager.ColdTabs(tabs, _ => true);

        Assert.Equal(3, cold.Count);
        Assert.All(tabs.Take(3), tab => Assert.Contains(tab, cold));
    }

    [Fact]
    public void SelectingATab_Should_MakeItWarm_AndPushOutTheOldestWarmOne()
    {
        var manager = new TabResidencyManager();
        var tabs = Open(manager, 5); // tab1, tab2 cold

        manager.Touch(tabs[0]);

        var cold = manager.ColdTabs(tabs, _ => true);
        Assert.DoesNotContain(tabs[0], cold);
        Assert.Contains(tabs[2], cold); // tab3 became the oldest of the warm ones
        Assert.Contains(tabs[1], cold);
    }

    [Fact]
    public void ATabThatCannotBeUnloaded_Should_StayOut_WithoutKeepingAnotherOneWarm()
    {
        var manager = new TabResidencyManager();
        var tabs = Open(manager, 5);

        var cold = manager.ColdTabs(tabs, tab => tab != tabs[0]);

        Assert.Equal([tabs[1]], cold);
    }

    [Fact]
    public void ATabNeverUsed_Should_CountAsTheOldest()
    {
        var manager = new TabResidencyManager();
        var tabs = Open(manager, 3);
        var unknown = new Tab("never touched");

        var cold = manager.ColdTabs(tabs.Append(unknown), _ => true);

        Assert.Equal([unknown], cold);
    }

    [Fact]
    public void AForgottenTab_Should_NoLongerBeRemembered()
    {
        var manager = new TabResidencyManager();
        var tabs = Open(manager, 4);

        manager.Forget(tabs[0]);

        Assert.Equal([tabs[0]], manager.ColdTabs(tabs, _ => true)); // unknown again: the oldest
    }

    [Fact]
    public void FewTabs_Should_NeverBeCold()
    {
        var manager = new TabResidencyManager();

        Assert.Empty(manager.ColdTabs(Open(manager, 3), _ => true));
    }
}
