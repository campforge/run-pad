using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace DotPad.Avalonia.CodeTextEditor;

/// <summary>A Tab-expandable <see cref="CodeTextEditor.Snippets"/> entry, surfaced as a completion
/// item too (shown on an explicit Ctrl+Space invoke — see CodeTextEditor.ShowCompletion) so snippets
/// are discoverable without already knowing their keyword.</summary>
public sealed class SnippetCompletionData(string keyword, string template, IImage? image, Action<ISegment> expand)
    : ICompletionData
{
    public string Text => keyword;
    public object Content => keyword;
    public object Description => template;
    public double Priority => 0.8;
    public IImage? Image => image;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => expand(completionSegment);
}
