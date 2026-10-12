using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #62 "occurrence highlighting" — end-to-end: CodeTextEditor.UpdateOccurrenceHighlights fires
// on every caret move and applies whatever AsyncOccurrencesProvider returns via
// OccurrenceHighlighter. The actual symbol-matching logic is covered at the provider level directly
// by CSharpIntelliSenseDocumentHighlightsTests.cs — these only need to prove the editor-side wiring
// (caret move -> provider call -> highlights applied/cleared), so a trivial fake provider is enough.
public class CodeTextEditorOccurrenceHighlightTests
{
    private static (Window Window, CodeTextEditor Editor) CreateEditor(string initialText)
    {
        var editor = new CodeTextEditor { Document = new TextDocument(initialText) };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        return (window, editor);
    }

    [AvaloniaFact]
    public async Task MovingTheCaret_Should_ApplyWhateverSpansTheProviderReturns()
    {
        var (_, editor) = CreateEditor("abc");
        editor.AsyncOccurrencesProvider = e =>
        {
            e.Spans.Add((0, 1));
            e.Spans.Add((2, 1));
            return Task.CompletedTask;
        };

        editor.TextArea.Caret.Offset = 1;
        await TestWait.WhileAsync(() => editor.HighlightedOccurrences.Count == 0);

        Assert.Equal([(0, 1), (2, 1)], editor.HighlightedOccurrences);
    }

    [AvaloniaFact]
    public async Task MovingTheCaret_Should_ClearPreviousHighlights_WhenTheProviderReturnsNone()
    {
        var (_, editor) = CreateEditor("abc");
        var returnSpans = true;
        editor.AsyncOccurrencesProvider = e =>
        {
            if (returnSpans) e.Spans.Add((0, 1));
            return Task.CompletedTask;
        };

        editor.TextArea.Caret.Offset = 1;
        await TestWait.WhileAsync(() => editor.HighlightedOccurrences.Count == 0);
        Assert.NotEmpty(editor.HighlightedOccurrences);

        returnSpans = false;
        editor.TextArea.Caret.Offset = 2;
        await TestWait.WhileAsync(() => editor.HighlightedOccurrences.Count > 0);

        Assert.Empty(editor.HighlightedOccurrences);
    }

    // Editor smoothness (2026-10-12): every typed character moves the caret, and each lookup is a full
    // semantic Roslyn query — typing must not run one per keystroke, only once the caret rests.
    [AvaloniaFact]
    public async Task RapidCaretMoves_LikeTyping_Should_QueryTheProviderOnce_ForTheFinalPosition()
    {
        var (_, editor) = CreateEditor("abcdefgh");
        var queried = new List<int>();
        editor.AsyncOccurrencesProvider = e =>
        {
            queried.Add(e.CaretOffset);
            e.Spans.Add((e.CaretOffset, 1));
            return Task.CompletedTask;
        };

        for (var offset = 1; offset <= 6; offset++)
            editor.TextArea.Caret.Offset = offset;
        await TestWait.WhileAsync(() => queried.Count == 0);
        await Task.Delay(editor.OccurrenceHighlightDelay * 2); // nothing else is still pending

        Assert.Equal([6], queried);
        Assert.Equal([(6, 1)], editor.HighlightedOccurrences);
    }
}
