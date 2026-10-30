using Invoklet.Services.QuickOpen;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;

internal sealed class QuickOpenSessionFixture
{
    public static readonly QuickOpenEntry[] Entries =
    [
        QuickOpenMatcherFixture.Entry("Alpha"),
        QuickOpenMatcherFixture.Entry("Beta", recentRank: 0),
        QuickOpenMatcherFixture.Entry("Gamma"),
    ];

    public QuickOpenSession CreateSut() => new(Entries);
}
