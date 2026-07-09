using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class CompletionRequestedEventArgs(char trigger, int caretOffset, bool isExplicitInvoke = false) : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;

    /// <summary>True when requested via an explicit invoke gesture (Ctrl+Space) rather than a
    /// trigger character being typed (e.g. '.').</summary>
    public bool IsExplicitInvoke { get; } = isExplicitInvoke;

    public List<ICompletionData> Items { get; } = [];
}
