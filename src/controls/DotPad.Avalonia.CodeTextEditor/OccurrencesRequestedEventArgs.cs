namespace DotPad.Avalonia.CodeTextEditor;

public sealed class OccurrencesRequestedEventArgs(int caretOffset, CancellationToken cancellationToken = default)
    : EventArgs
{
    public int CaretOffset { get; } = caretOffset;

    /// <summary>Cancelled if a newer request supersedes this one before it resolves (fast caret
    /// movement past the first request — same reasoning as CompletionRequestedEventArgs).</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Spans (offset, length) of every occurrence to highlight, in the caller's own
    /// document coordinates.</summary>
    public List<(int Start, int Length)> Spans { get; } = [];
}
