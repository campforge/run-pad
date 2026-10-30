using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

internal sealed class RefactoringRequestsFixture
{
    public Mock<IIntelliSenseService> IntelliSense { get; } = new();

    public IReadOnlyList<ICodeFixInfo> SetupRefactorings(string text, int offset)
    {
        IReadOnlyList<ICodeFixInfo> refactorings = [new Mock<ICodeFixInfo>().Object];
        IntelliSense.Setup(i => i.GetRefactoringsAsync(text, offset, It.IsAny<CancellationToken>())).ReturnsAsync(refactorings);
        return refactorings;
    }

    /// <summary>
    /// Makes the language service wait until the answer is completed. Received is completed with the token of
    /// each call as the service gets it, so a test waits for that instead of guessing when the request got there.
    /// </summary>
    public (TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?> Answer, Task<CancellationToken> Received) SetupPendingAnswer()
    {
        var answer = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?>();
        var received = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        IntelliSense.Setup(i => i.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int _, CancellationToken token) =>
            {
                received.TrySetResult(token);
                return answer.Task;
            });
        return (answer, received.Task);
    }

    public RefactoringRequests CreateSut(TimeSpan? debounce = null) => new(IntelliSense.Object, debounce ?? TimeSpan.Zero);
}
