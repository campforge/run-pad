using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.UserControls;

// ToDo: move to Invoklet.Avalonia.CodeTextEditor (same tech debt as SemanticHighlighter)
// Renders IInlayHint.Text as dim, non-editable inline decoration at
// IInlayHint.Offset. Same technique AvaloniaEdit's own FoldingElementGenerator ("...") and
// SingleCharacterElementGenerator (space/tab glyphs) use — a zero-document-length FormattedTextElement,
// a pure visual insertion between two existing characters, never part of the actual document text.
internal sealed class InlayHintRenderer : VisualLineElementGenerator
{
    public static IBrush DefaultBrush { get; } = Brushes.Gray;

    // Preferences.CSharp.InlayHintsEnabled — gates rendering only, not
    // computation: CSharpIntelliSenseService always computes hints regardless of this flag (it has
    // no knowledge of IPreferencesService at all, same separation as every other IntelliSense
    // feature), the UI layer just doesn't render them here when disabled.
    public bool IsEnabled { get; set; } = true;

    // Tracked zero-length segments, not absolute offsets: typing shifts them, so a hint stays in front
    // of its identifier until the next analysis instead of jumping into the middle of other text.
    private readonly TrackedSegments<HintSegment> _hints = new();

    /// <summary>Replaces the hints without following edits (pure tests); see the document overload.</summary>
    public void Update(IReadOnlyList<IInlayHint> hints) => Update(document: null, hints);

    /// <summary>
    /// Replaces the hints, following <paramref name="document"/>'s edits from now on, and returns the
    /// span whose hints changed — the part to redraw (hints change line widths) — or null.
    /// </summary>
    public (int Start, int End)? Update(AvaloniaEdit.Document.TextDocument? document, IReadOnlyList<IInlayHint> hints)
    {
        var before = _hints.All;
        _hints.Replace(document, hints
            .OrderBy(h => h.Offset)
            .Select(h => new HintSegment(h.Text) { StartOffset = h.Offset, Length = 0 }));
        return TrackedSegments<HintSegment>.ChangedRange(before, _hints.All, (a, b) => a.Text == b.Text);
    }

    internal IReadOnlyList<HintSegment> Hints => _hints.All;

    public override int GetFirstInterestedOffset(int startOffset) =>
        IsEnabled && _hints.FirstStartingAtOrAfter(startOffset) is { } hint ? hint.StartOffset : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_hints.FirstStartingAtOrAfter(offset) is not { } hint || hint.StartOffset != offset) return null;

        var properties = new VisualLineElementTextRunProperties(CurrentContext.GlobalTextRunProperties);
        properties.SetForegroundBrush(DefaultBrush);
        var textSource = new HintTextSource(hint.Text, properties);
        var textLine = TextFormatter.Current.FormatLine(
            textSource, 0, double.MaxValue, new GenericTextParagraphProperties(properties));
        return new FormattedTextElement(textLine, 0);
    }

    internal sealed class HintSegment(string text) : AvaloniaEdit.Document.TextSegment
    {
        public string Text { get; } = text;
    }

    // AvaloniaEdit's own equivalent (AvaloniaEdit.Rendering.SimpleTextSource, the exact pattern
    // FoldingElementGenerator/SingleCharacterElementGenerator use for this same purpose) is internal
    // to that assembly, unreachable from here — this is the same ~10-line implementation.
    private sealed class HintTextSource(string text, TextRunProperties properties) : ITextSource
    {
        public TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex < text.Length)
                return new TextCharacters(text.AsMemory()[textSourceCharacterIndex..], properties);
            return new TextEndOfParagraph(1);
        }
    }
}
