namespace DotPad.Avalonia.CodeTextEditor;

public sealed class OverloadRequestedEventArgs(char trigger, int caretOffset) : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;
    public List<(object Header, object? Content)> Overloads { get; } = [];
}
