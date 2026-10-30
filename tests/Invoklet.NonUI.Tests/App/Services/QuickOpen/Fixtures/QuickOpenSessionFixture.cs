using Invoklet.Services.QuickOpen;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;

internal sealed class QuickOpenSessionFixture
{
    public static readonly QuickOpenEntry[] Entries =
    [
        QuickOpenMatcherFixture.Entry("Alpha"),
        QuickOpenMatcherFixture.Entry("Beta", recentRank: 0),
        QuickOpenMatcherFixture.Entry("Gamma"),
        QuickOpenMatcherFixture.Entry("Sample One", kind: QuickOpenKind.Sample),
    ];

    public static QuickOpenEntry Other { get; } = QuickOpenMatcherFixture.Entry("notes.md", kind: QuickOpenKind.OtherFile);

    public QuickOpenSession CreateSut() => new(Entries);
}
