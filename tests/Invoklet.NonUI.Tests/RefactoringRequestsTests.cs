using Invoklet.Contracts.IntelliSense;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class RefactoringRequestsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RequestAsync_PositionSurvivesTheDebounce_ReturnsWhatTheServiceFoundThere()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        var expected = fixture.SetupRefactorings("code", 3);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RequestAsync(() => "code", 3);

        // Assert
        Assert.Same(expected, result.Refactorings);
        Assert.True(sut.IsCurrent(result));
    }

    [Fact]
    public async Task RequestAsync_NothingAtThePosition_ReturnsNull()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        fixture.IntelliSense.Setup(i => i.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ICodeFixInfo>?)null);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RequestAsync(() => "code", 3);

        // Assert
        Assert.Null(result.Refactorings);
    }

    [Fact]
    public async Task RequestAsync_ANewerRequestComesDuringTheDebounce_NeverReadsTheTextNorAsksTheService()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        fixture.SetupRefactorings("code", 4);
        var sut = fixture.CreateSut(debounce: TimeSpan.FromHours(1));
        var textReads = 0;
        var superseded = sut.RequestAsync(() => { textReads++; return "code"; }, 3);

        // Act
        _ = sut.RequestAsync(() => "code", 4); // the caret moved again
        var cancelled = () => superseded.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(cancelled);
        Assert.Equal(0, textReads);
        fixture.IntelliSense.Verify(i => i.GetRefactoringsAsync(It.IsAny<string>(), 3, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestAsync_ANewerRequestComesWhileTheServiceWorks_CancelsItAndDropsItsAnswer()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        var (answer, received) = fixture.SetupPendingAnswer();
        var sut = fixture.CreateSut();
        var first = sut.RequestAsync(() => "code", 3);
        var token = await received.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        _ = sut.RequestAsync(() => "code", 4);
        answer.SetResult([new Mock<ICodeFixInfo>().Object]);
        var finished = () => first.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(finished);
    }

    [Fact]
    public async Task Cancel_RequestInFlight_CancelsItsToken()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        var (_, received) = fixture.SetupPendingAnswer();
        var sut = fixture.CreateSut();
        _ = sut.RequestAsync(() => "code", 3);
        var token = await received.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        sut.Cancel();

        // Assert
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_RequestAlreadyFinished_DoesNothingAndMakesItsResultStale()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        fixture.SetupRefactorings("code", 3);
        var sut = fixture.CreateSut();
        var result = await sut.RequestAsync(() => "code", 3);
        var currentBefore = sut.IsCurrent(result);

        // Act
        sut.Cancel(); // must not touch the source of the finished request, which it has released

        // Assert
        Assert.True(currentBefore);
        Assert.False(sut.IsCurrent(result));
    }

    [Fact]
    public async Task IsCurrent_ANewerRequestStarted_IsFalseForTheOlderResult()
    {
        // Arrange
        var fixture = new RefactoringRequestsFixture();
        fixture.SetupRefactorings("code", 3);
        var sut = fixture.CreateSut();
        var result = await sut.RequestAsync(() => "code", 3);

        // Act
        _ = sut.RequestAsync(() => "code", 4);

        // Assert
        Assert.False(sut.IsCurrent(result));
    }
}
