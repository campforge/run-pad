namespace RunPad.Avalonia.CodeTextEditor;

public sealed class OverloadRequestedEventArgs(char trigger, int caretOffset, CancellationToken cancellationToken = default)
    : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;

    /// <summary>Cancelled if a newer overload request supersedes this one before it resolves — see
    /// CompletionRequestedEventArgs.CancellationToken/ANALYSIS.md #57.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public List<(object Header, object? Content)> Overloads { get; } = [];
}
