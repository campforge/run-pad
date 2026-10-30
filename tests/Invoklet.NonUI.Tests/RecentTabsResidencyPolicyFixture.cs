using Invoklet.Services.Tabs;

namespace Invoklet.NonUI.Tests;

// The SUT has no injected dependencies: the fixture owns the test data (tabs, which the policy only compares by
// identity) and creates a new instance for each test.
public sealed class RecentTabsResidencyPolicyFixture
{
    public sealed class Tab(string name)
    {
        public override string ToString() => name;
    }

    public List<Tab> CreateTabs(int count) => Enumerable.Range(1, count).Select(i => new Tab("tab" + i)).ToList();

    public RecentTabsResidencyPolicy CreateSut(int? warmTabs = null)
        => warmTabs is { } warm ? new RecentTabsResidencyPolicy(warm) : new RecentTabsResidencyPolicy();
}
