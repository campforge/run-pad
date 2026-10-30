using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeTextEditor;

// Brace matching runs on every caret move (every keystroke). It used to copy the whole document into a
// string each time; it now reads outward from the brace in 4 KB chunks (editor smoothness, 2026-10-12).
// These pin its results down, including matches across chunk boundaries in both directions.
public class CodeTextEditorBraceMatchTests
{
    // the pairs come from the language's EditorLanguageProfile; a bare editor has none — use C#'s
    private static Invoklet.Avalonia.CodeTextEditor.CodeTextEditor Editor(string text)
    {
        var editor = new Invoklet.Avalonia.CodeTextEditor.CodeTextEditor { Document = new TextDocument(text) };
        editor.BracePairs['('] = ')';
        editor.BracePairs['['] = ']';
        editor.BracePairs['{'] = '}';
        return editor;
    }

    [AvaloniaTheory]
    [InlineData("a(b)c", 1, 1, 3, true)]      // caret on '(' — search forward
    [InlineData("a(b)c", 4, 1, 3, true)]      // caret after ')' — search backward
    [InlineData("{ [ ( ) ] }", 0, 0, 10, true)] // nested different pairs
    [InlineData("((x))", 1, 1, 3, true)]      // nested same pair
    [InlineData("(x", 0, 0, -1, false)]       // unmatched open
    [InlineData("x)", 2, -1, 1, false)]       // unmatched close
    public void FindBraceMatch_Should_FindTheMatchingBrace(string text, int caret, int open, int close, bool matched)
    {
        Assert.Equal((open, close, matched), Editor(text).FindBraceMatch(caret));
    }

    [AvaloniaFact]
    public void FindBraceMatch_Should_ReturnNothing_AwayFromBraces()
    {
        Assert.Equal((-1, -1, false), Editor("abc").FindBraceMatch(1));
        Assert.Equal((-1, -1, false), Editor("").FindBraceMatch(0));
    }

    [AvaloniaFact]
    public void FindBraceMatch_Should_FindAMatch_SeveralChunksAway_InBothDirections()
    {
        // 10 000 characters of nested noise between the braces: the match is ~3 chunks away
        var body = string.Concat(Enumerable.Repeat("(x)", 3334));
        var text = "{" + body + "}";
        var editor = Editor(text);

        Assert.Equal((0, text.Length - 1, true), editor.FindBraceMatch(0));             // forward
        Assert.Equal((0, text.Length - 1, true), editor.FindBraceMatch(text.Length));   // backward
    }

    [AvaloniaFact]
    public void FindBraceMatch_Should_FindAMatch_ExactlyAcrossAChunkBoundary()
    {
        // the closing brace is the first character of the second 4096-character chunk
        var text = "(" + new string('x', 4096) + ")";
        var editor = Editor(text);

        Assert.Equal((0, 4097, true), editor.FindBraceMatch(0));
        Assert.Equal((0, 4097, true), editor.FindBraceMatch(4098));
    }
}
