namespace DotPad.Avalonia.CodeTextEditor;

public sealed class OverloadRequestedEventArgs(char trigger, int caretOffset) : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;
    public List<(string Header, string? Description)> Overloads { get; } = [];
}
