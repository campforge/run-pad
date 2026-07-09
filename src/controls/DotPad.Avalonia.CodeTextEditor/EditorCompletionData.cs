using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DotPad.Contracts.IntelliSense;

namespace DotPad;

internal sealed class EditorCompletionData(
    string displayText,
    string description,
    double priority
) : ICompletionData
{
    public string Text => displayText;

    public object Content => displayText;

    public object Description => description;

    public double Priority => priority;

    public IImage? Image => null;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, displayText);
    }
}
