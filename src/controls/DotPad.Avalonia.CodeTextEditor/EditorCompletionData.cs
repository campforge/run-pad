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
// onCommit/tags let a language-aware caller attach a side effect to acceptance without this class
// (or CodeTextEditor) knowing what the tags mean — e.g. EditorTabItemControl uses them to auto-add a
// `using` when the accepted item is a type from an added-but-not-yet-imported NuGet package
// (TODO.md #28 remainder). Empty/null tags are the common case and cost nothing extra.
public sealed class EditorCompletionData(
    string displayText,
    Func<object> descriptionFactory,
    double priority,
    IImage? image = null,
    IReadOnlyList<string>? tags = null,
    Action<IReadOnlyList<string>>? onCommit = null
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
        if (tags is { Count: > 0 })
            onCommit?.Invoke(tags);
    }
}
