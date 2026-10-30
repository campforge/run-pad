using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

/// <summary>The refactorings the language service found at a caret position, and the token of the request they belong to.</summary>
internal sealed record RefactoringResult(IReadOnlyList<ICodeFixInfo>? Refactorings, CancellationToken Request);

/// <summary>
/// Asks the language service for the refactorings at the caret. Every caret move asks again, so a request waits
/// out a debounce first and a newer request cancels the one before it: only the last position is ever analysed
/// and only its answer is returned (a superseded request ends with <see cref="OperationCanceledException"/>).
/// </summary>
internal sealed class RefactoringRequests(IIntelliSenseService intelliSense, TimeSpan debounce)
{
    // Not disposed: a superseded request may still be reading its token, and a source nobody waits on holds no
    // unmanaged resources.
    private CancellationTokenSource? _current;

    /// <param name="text">Reads the text of the document; called only if the position survives the debounce, so it costs nothing per keystroke.</param>
    public async Task<RefactoringResult> RequestAsync(Func<string> text, int offset)
    {
        var request = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, request)?.Cancel();
        var token = request.Token;

        await Task.Delay(debounce, token);
        var refactorings = await intelliSense.GetRefactoringsAsync(text(), offset, token);
        token.ThrowIfCancellationRequested();
        return new RefactoringResult(refactorings, token);
    }

    /// <summary>Cancels the request in flight, if any.</summary>
    public void Cancel() => Interlocked.Exchange(ref _current, null)?.Cancel();
}
