using Invoklet.NonUI.Tests.App.Services.QuickOpen.Fixtures;
using Invoklet.Services.QuickOpen;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen;

public sealed class QuickOpenContentSearchTests
{
    private static readonly QuickOpenEntry[] Entries =
    [
        QuickOpenMatcherFixture.Entry("A"),
        QuickOpenMatcherFixture.Entry("B"),
        QuickOpenMatcherFixture.Entry("C"),
    ];

    private static Task<string?> Read(QuickOpenEntry entry, CancellationToken ct) => Task.FromResult<string?>(entry.Name switch
    {
        "A" => "var total = Sum();",
        "B" => "Console.WriteLine(1);",
        _ => null,
    });

    [Fact]
    public async Task FindAsync_TextInTheContents_ReturnsThoseEntriesInOrderWhateverTheCase()
    {
        // Arrange / Act
        var found = await QuickOpenContentSearch.FindAsync(Entries, "TOTAL", Read, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["A"], found.Select(e => e.Name));
    }

    [Fact]
    public async Task FindAsync_AnEntryThatCannotBeRead_IsSkipped()
    {
        // Arrange / Act
        var found = await QuickOpenContentSearch.FindAsync(Entries, "(", Read, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["A", "B"], found.Select(e => e.Name));
    }

    [Fact]
    public async Task FindAsync_NothingTyped_FindsNothingAndReadsNothing()
    {
        // Arrange
        var reads = 0;

        // Act
        var found = await QuickOpenContentSearch.FindAsync(Entries, "  ", (e, ct) =>
        {
            reads++;
            return Task.FromResult<string?>("x");
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(found);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task FindAsync_MoreMatchesThanTheLimit_StopsAtIt()
    {
        // Arrange / Act
        var found = await QuickOpenContentSearch.FindAsync(Entries, "x", (e, ct) => Task.FromResult<string?>("x"),
            TestContext.Current.CancellationToken, limit: 2);

        // Assert
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task FindAsync_Cancelled_Throws()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        var thrown = await Record.ExceptionAsync(() => QuickOpenContentSearch.FindAsync(Entries, "x", Read, cancelled.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }
}
