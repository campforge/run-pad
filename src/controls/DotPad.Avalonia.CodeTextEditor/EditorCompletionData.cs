using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace DotPad.Avalonia.CodeTextEditor;

// descriptionFactory is called lazily, only when Description is actually read (AvaloniaEdit reads it
// once an item is highlighted in the list) — not for every item up front. Building the rich
// Description control (DisplaySegmentRenderer.BuildWithSummary — real Avalonia TextBlock/Run
// construction) eagerly for, say, ~584 completion items on every keystroke was the main perceived-
// latency contributor in the 24/7 auto-invoke path (ANALYSIS.md TODO #56); only one item's
// Description is ever visible at a time.
public sealed class EditorCompletionData(
    string displayText,
    Func<object> descriptionFactory,
    double priority,
    IImage? image = null
) : ICompletionData
{
    private readonly Lazy<object> _description = new(descriptionFactory);

    public string Text => displayText;

    public object Content => displayText;

    public object Description => _description.Value;

    public double Priority => priority;

    public IImage? Image => image;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, displayText);
    }
}
