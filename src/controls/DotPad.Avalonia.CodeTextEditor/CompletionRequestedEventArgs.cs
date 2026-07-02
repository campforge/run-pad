using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class CompletionRequestedEventArgs(char trigger, int caretOffset) : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;
    public List<ICompletionData> Items { get; } = [];
}
