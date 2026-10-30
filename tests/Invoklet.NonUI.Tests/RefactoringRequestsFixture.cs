using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class RefactoringRequestsFixture
{
    public Mock<IIntelliSenseService> IntelliSense { get; } = new();

    public IReadOnlyList<ICodeFixInfo> SetupRefactorings(string text, int offset)
    {
        IReadOnlyList<ICodeFixInfo> refactorings = [new Mock<ICodeFixInfo>().Object];
        IntelliSense.Setup(i => i.GetRefactoringsAsync(text, offset, It.IsAny<CancellationToken>())).ReturnsAsync(refactorings);
        return refactorings;
    }

    /// <summary>Makes the language service wait until the returned source is completed, and records the tokens it got.</summary>
    public TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?> SetupPendingAnswer(List<CancellationToken> tokens)
    {
        var answer = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?>();
        IntelliSense.Setup(i => i.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int _, CancellationToken token) =>
            {
                tokens.Add(token);
                return answer.Task;
            });
        return answer;
    }

    public RefactoringRequests CreateSut(TimeSpan? debounce = null) => new(IntelliSense.Object, debounce ?? TimeSpan.Zero);
}
