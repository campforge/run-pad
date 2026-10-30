using Invoklet.Services.QuickOpen;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;

// The matcher is a pure function of its entries and the query: the fixture only builds entries.
internal sealed class QuickOpenMatcherFixture
{
    public static QuickOpenEntry Entry(string name, string folder = "", int recentRank = -1)
        => new(Path.Combine("/scripts", folder, name + ".inv"), name, folder, recentRank);

    public static string[] Names(IEnumerable<QuickOpenEntry> entries) => [.. entries.Select(e => e.Name)];
}
