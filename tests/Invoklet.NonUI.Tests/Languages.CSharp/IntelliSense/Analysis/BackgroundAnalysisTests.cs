using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class BackgroundAnalysisTests
{
    [Fact]
    public async Task AnalyzeAsync_CodeWithAnError_PublishesTheDiagnosticAtItsPlaceAndIsCurrent()
    {
        // Arrange
        using var fixture = new BackgroundAnalysisFixture();
        var sut = fixture.CreateSut();
        const string code = "int a = \"x\";";

        // Act
        var current = await sut.AnalyzeAsync(code, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(current);
        var published = Assert.Single(fixture.Diagnostics);
        Assert.Contains(published, d => d.StartOffset == code.IndexOf("\"x\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_ValidCode_PublishesNoDiagnosticsAndTheTokensOfTheNames()
    {
        // Arrange
        using var fixture = new BackgroundAnalysisFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.AnalyzeAsync("class C { }", TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Assert.Single(fixture.Diagnostics));
        Assert.NotEmpty(Assert.Single(fixture.Tokens));
    }

    [Fact]
    public async Task AnalyzeAsync_ContextChangesAfterTheSnapshot_PublishesNothingAndAsksForARepeat()
    {
        // Arrange
        using var fixture = new BackgroundAnalysisFixture();
        var sut = fixture.CreateSut();
        sut.SnapshotTakenForTests = fixture.Session.ContextChanged;

        // Act
        var current = await sut.AnalyzeAsync("int a = \"x\";", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(current);
        Assert.Empty(fixture.Diagnostics);
        Assert.Empty(fixture.Tokens);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancelled_ThrowsAndPublishesNothing()
    {
        // Arrange
        using var fixture = new BackgroundAnalysisFixture();
        var sut = fixture.CreateSut();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var thrown = await Record.ExceptionAsync(() => sut.AnalyzeAsync("int a = 1;", cancelled.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Empty(fixture.Diagnostics);
    }

    [Fact]
    public async Task AnalyzeAsync_StoppedWhileThePassRuns_PublishesNothing()
    {
        // Arrange
        using var fixture = new BackgroundAnalysisFixture();
        var sut = fixture.CreateSut();
        sut.SnapshotTakenForTests = sut.Stop;

        // Act
        var current = await sut.AnalyzeAsync("int a = \"x\";", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(current);
        Assert.Empty(fixture.Diagnostics);
        Assert.Empty(fixture.Tokens);
    }
}
