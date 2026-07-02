using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DotPad.Contracts.IntelliSense;

namespace DotPad;

internal sealed class EditorCompletionData(ICompletionItem item) : ICompletionData
{
    public string Text => item.DisplayText;

    public object Content => item.DisplayText;

    public object Description => item.Description ?? string.Empty;

    public double Priority => item.Kind == CompletionItemKind.Method ? 1.0 : 0.9;

    public IBrush? Background => null;

    public IImage? Image => null;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, item.DisplayText);
    }
}
