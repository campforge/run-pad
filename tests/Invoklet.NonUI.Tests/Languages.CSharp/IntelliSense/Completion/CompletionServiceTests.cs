using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class CompletionServiceTests
{
    [Fact]
    public async Task GetCompletionsAsync_ALocalInScope_IsOfferedByItsPrefix()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateCompletion();
        const string code = "var counter = 1;\ncou";

        // Act
        var list = await sut.GetCompletionsAsync(code, code.Length, default, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(list!.Items, i => i.DisplayText == "counter");
    }

    [Fact]
    public async Task GetCompletionsAsync_InsideAComment_OffersNothing()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateCompletion();
        const string code = "var counter = 1;\n// cou";

        // Act
        var list = await sut.GetCompletionsAsync(code, code.Length, default, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(list is null || list.Items.Count == 0);
    }

    [Fact]
    public async Task GetCompletionsAsync_AfterArgumentsDot_OffersTheMembersOfTheContract()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        fixture.Context.UpdateArgumentContract(new MdRunArgumentContract([new MdRunArgumentContractMember("name", "string", [], [])]));
        var sut = fixture.CreateCompletion();
        const string code = "Arguments.";

        // Act
        var list = await sut.GetCompletionsAsync(code, code.Length, default, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(list!.Items, i => i.DisplayText == "Name");
    }

    [Fact]
    public async Task GetCompletionsAsync_TheContextChangesAfterTheSnapshot_ComputesAgainOnTheNewSolution()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateCompletion();
        var snapshots = 0;
        sut.SnapshotTakenForTests = () =>
        {
            if (++snapshots == 1) fixture.Session.ContextChanged();
        };

        // Act
        await sut.GetCompletionsAsync("var counter = 1;\ncou", 21, default, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, snapshots);
    }
}
