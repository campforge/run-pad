using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Invoklet.Avalonia.CodeTextEditor;

public sealed class CompletionData : ICompletionData
{
    public CompletionData(string text) => Text = text;

    public IImage? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description => "Description for " + Text;
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}
