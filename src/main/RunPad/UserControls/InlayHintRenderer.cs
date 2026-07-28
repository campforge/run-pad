using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

// ToDo: перенести в RunPad.Avalonia.CodeTextEditor (тот же долг, что у SemanticHighlighter)
// TODO.md #62 "inlay hints" — renders IInlayHint.Text as dim, non-editable inline decoration at
// IInlayHint.Offset. Same technique AvaloniaEdit's own FoldingElementGenerator ("...") and
// SingleCharacterElementGenerator (space/tab glyphs) use — a zero-document-length FormattedTextElement,
// a pure visual insertion between two existing characters, never part of the actual document text.
internal sealed class InlayHintRenderer : VisualLineElementGenerator
{
    public static IBrush DefaultBrush { get; } = Brushes.Gray;

    // Preferences.CSharp.InlayHintsEnabled (user request 2026-07-20) — gates rendering only, not
    // computation: CSharpIntelliSenseService always computes hints regardless of this flag (it has
    // no knowledge of IPreferencesService at all, same separation as every other IntelliSense
    // feature), the UI layer just doesn't render them here when disabled.
    public bool IsEnabled { get; set; } = true;

    private IReadOnlyList<IInlayHint> _hints = [];

    public void Update(IReadOnlyList<IInlayHint> hints) => _hints = hints;

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!IsEnabled) return -1;

        var best = -1;
        foreach (var hint in _hints)
        {
            if (hint.Offset < startOffset) continue;
            if (best == -1 || hint.Offset < best) best = hint.Offset;
        }
        return best;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var hint in _hints)
        {
            if (hint.Offset != offset) continue;

            var properties = new VisualLineElementTextRunProperties(CurrentContext.GlobalTextRunProperties);
            properties.SetForegroundBrush(DefaultBrush);
            var textSource = new HintTextSource(hint.Text, properties);
            var textLine = TextFormatter.Current.FormatLine(
                textSource, 0, double.MaxValue, new GenericTextParagraphProperties(properties));
            return new FormattedTextElement(textLine, 0);
        }
        return null;
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
