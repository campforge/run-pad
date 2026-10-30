using Invoklet.Contracts.Models;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class WatchEvaluatorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateAsync_EmptyExpression_ShowsAQuestionMarkWithoutAskingTheDebugger(string expression)
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var watch = WatchEvaluatorFixture.CreateWatch(expression);
        var sut = fixture.CreateSut();

        // Act
        await sut.EvaluateAsync(watch);

        // Assert
        Assert.Equal("?", watch.Value);
        context.Verify(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_NotPaused_ShowsThatItIsNotPaused()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();

        // Act
        await sut.EvaluateAsync(watch);

        // Assert
        Assert.Equal("<not paused>", watch.Value);
        Assert.Equal("", watch.TypeName);
    }

    [Fact]
    public async Task EvaluateAsync_DebuggerAnswers_ShowsValueAndType()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        context.Setup(c => c.EvaluateAsync("x + 1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WatchEvaluatorFixture.CreateResult("42", "int"));
        var watch = WatchEvaluatorFixture.CreateWatch("x + 1");
        var sut = fixture.CreateSut();

        // Act
        await sut.EvaluateAsync(watch);

        // Assert
        Assert.Equal("42", watch.Value);
        Assert.Equal("int", watch.TypeName);
    }

    [Fact]
    public async Task EvaluateAsync_DebuggerCannotEvaluate_ShowsError()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((IVariableInfo?)null);
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();

        // Act
        await sut.EvaluateAsync(watch);

        // Assert
        Assert.Equal("<error>", watch.Value);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluationThrows_ShowsTheMessage()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();

        // Act
        await sut.EvaluateAsync(watch);

        // Assert
        Assert.Equal("<error: boom>", watch.Value);
    }

    [Fact]
    public async Task EvaluateAsync_StopMovedOnBeforeTheAnswer_DropsTheAnswer()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var answer = new TaskCompletionSource<IVariableInfo?>();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(answer.Task);
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();
        var evaluation = sut.EvaluateAsync(watch);

        // Act
        fixture.Context = fixture.CreateContext().Object;
        answer.SetResult(WatchEvaluatorFixture.CreateResult("old", "int"));
        await evaluation.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("?", watch.Value);
    }

    [Fact]
    public async Task EvaluateAsync_RowRemovedBeforeTheAnswer_DropsTheAnswer()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var answer = new TaskCompletionSource<IVariableInfo?>();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(answer.Task);
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();
        var evaluation = sut.EvaluateAsync(watch);

        // Act
        fixture.Listed = false;
        answer.SetResult(WatchEvaluatorFixture.CreateResult("late", "int"));
        await evaluation.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("?", watch.Value);
    }

    [Fact]
    public async Task EvaluateAsync_ASecondEvaluationStarts_CancelsTheFirst()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var tokens = new List<CancellationToken>();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                tokens.Add(token);
                return new TaskCompletionSource<IVariableInfo?>().Task;
            });
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();

        // Act
        _ = sut.EvaluateAsync(watch);
        _ = sut.EvaluateAsync(watch);

        // Assert
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_FirstEvaluationEndsAfterTheSecondStarted_SecondCanStillBeCancelled()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var answers = new List<TaskCompletionSource<IVariableInfo?>>();
        var tokens = new List<CancellationToken>();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                tokens.Add(token);
                var answer = new TaskCompletionSource<IVariableInfo?>();
                answers.Add(answer);
                return answer.Task;
            });
        var watch = WatchEvaluatorFixture.CreateWatch("x");
        var sut = fixture.CreateSut();
        var first = sut.EvaluateAsync(watch);
        _ = sut.EvaluateAsync(watch);
        answers[0].SetCanceled(tokens[0]);
        await first.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Act
        sut.Cancel(watch);

        // Assert
        Assert.True(tokens[1].IsCancellationRequested);
    }

    [Fact]
    public void CancelAll_EvaluationsRunning_CancelsEachOfThem()
    {
        // Arrange
        var fixture = new WatchEvaluatorFixture();
        var context = fixture.CreateContext();
        var tokens = new List<CancellationToken>();
        context.Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                tokens.Add(token);
                return new TaskCompletionSource<IVariableInfo?>().Task;
            });
        var sut = fixture.CreateSut();
        _ = sut.EvaluateAsync(WatchEvaluatorFixture.CreateWatch("a"));
        _ = sut.EvaluateAsync(WatchEvaluatorFixture.CreateWatch("b"));

        // Act
        sut.CancelAll();

        // Assert
        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
    }
}
