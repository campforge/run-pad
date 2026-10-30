using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class LatestRequestTests
{
    [Fact]
    public void Invalidate_Twice_MakesTheFirstGenerationStale()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        var first = sut.Invalidate();

        // Act
        var second = sut.Invalidate();

        // Assert
        Assert.False(sut.IsCurrent(first));
        Assert.True(sut.IsCurrent(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewToken_AfterInvalidate_IsNotCancelledWhileThatRequestIsTheNewest()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        sut.Invalidate();

        // Act
        var token = sut.NewToken();

        // Assert
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Invalidate_RequestInFlight_CancelsItsToken()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        sut.Invalidate();
        var token = sut.NewToken();

        // Act
        sut.Invalidate();

        // Assert
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Invalidate_NoRequestInFlight_OnlyStartsANewGeneration()
    {
        // Arrange (a request that returned early and never asked for a token)
        var sut = new LatestRequestFixture().CreateSut();
        var first = sut.Invalidate();

        // Act
        var second = sut.Invalidate();

        // Assert
        Assert.False(sut.IsCurrent(first));
        Assert.True(sut.IsCurrent(second));
    }

    [Fact]
    public void Dispose_RequestInFlight_CancelsItsToken()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        sut.Invalidate();
        var token = sut.NewToken();

        // Act
        sut.Dispose();

        // Assert
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_Twice_DoesNothingTheSecondTime()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        sut.Invalidate();
        sut.NewToken();
        sut.Dispose();

        // Act
        var disposeAgain = sut.Dispose;

        // Assert
        var exception = Record.Exception(disposeAgain);
        Assert.Null(exception);
    }

    [Fact]
    public async Task IsCurrent_ReadFromAnotherThreadWhileInvalidated_SeesTheNewGeneration()
    {
        // Arrange
        var sut = new LatestRequestFixture().CreateSut();
        var generation = sut.Invalidate();

        // Act
        sut.Invalidate();
        var current = await Task.Run(() => sut.IsCurrent(generation), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(current);
    }
}
