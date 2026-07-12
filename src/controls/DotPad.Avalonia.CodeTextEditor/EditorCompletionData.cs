using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class EditorCompletionData(
    string displayText,
    object description,
    double priority,
    IImage? image = null
) : ICompletionData
{
    public string Text => displayText;

    public object Content => displayText;

    public object Description => description;

    public double Priority => priority;

    public IImage? Image => image;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, displayText);
    }
}
