using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

/// <summary>The refactorings the language service found at a caret position, and which request they answer.</summary>
internal sealed record RefactoringResult(IReadOnlyList<ICodeFixInfo>? Refactorings, long Generation);

/// <summary>
/// Asks the language service for the refactorings at the caret. Every caret move asks again, so a request waits
/// out a debounce first and a newer request cancels the one before it: only the last position is ever analysed
/// and only its answer is returned (a superseded request ends with <see cref="OperationCanceledException"/>).
/// </summary>
internal sealed class RefactoringRequests(IIntelliSenseService intelliSense, TimeSpan debounce)
{
    private CancellationTokenSource? _current;
    private long _generation;

    /// <param name="text">Reads the text of the document; called only if the position survives the debounce, so it costs nothing per keystroke.</param>
    public async Task<RefactoringResult> RequestAsync(Func<string> text, int offset)
    {
        using var request = new CancellationTokenSource();
        var generation = Supersede(request);
        var token = request.Token;
        try
        {
            await Task.Delay(debounce, token);
            var refactorings = await intelliSense.GetRefactoringsAsync(text(), offset, token);
            token.ThrowIfCancellationRequested();
            return new RefactoringResult(refactorings, generation);
        }
        finally
        {
            // Only this request's own source, and only now that nothing of it can read the token any more.
            Interlocked.CompareExchange(ref _current, null, request);
        }
    }

    /// <summary>Whether no request has been made, and none cancelled, since the one that gave <paramref name="result"/>; the answer is then still wanted.</summary>
    public bool IsCurrent(RefactoringResult result) => Volatile.Read(ref _generation) == result.Generation;

    /// <summary>Cancels the request in flight, if any.</summary>
    public void Cancel() => Supersede(null);

    private long Supersede(CancellationTokenSource? next)
    {
        var previous = Interlocked.Exchange(ref _current, next);
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished and released its source between the exchange and here: nothing left to cancel.
        }

        return generation;
    }
}
