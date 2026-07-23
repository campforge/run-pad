using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// Live user report ("если нажать ctrl+c и нет выбранных символов, то копируется вся строка, вместе
// с переносом" / same for Ctrl+X): the forked AvaloniaEdit's EditingCommandHandler already implements
// this exact behavior (OnCopy/OnCut fall back to CopyWholeLine when TextArea.Selection.IsEmpty), gated
// behind TextEditorOptions.CutCopyWholeLine, whose class-level default is already `true` — and DotPad's
// CodeTextEditor never overrides it. Nothing here was DotPad-specific code to write; these tests exist
// to empirically confirm the already-shipped-upstream behavior actually reaches a real DotPad editor
// end-to-end (real Ctrl+C/Ctrl+X keypress through Avalonia.Headless, not just calling the command
// method directly), since neither this behavior nor Options.CutCopyWholeLine had any test coverage
// before, upstream (AvaloniaEdit.Tests) or in DotPad.
public class EditorClipboardWholeLineTests
{
    private static (Window Window, CodeTextEditor Editor) CreateEditorWithText(string text)
    {
        var editor = new CodeTextEditor();
        var window = new Window { Content = editor };
        window.Show();
        editor.Text = text;
        editor.TextArea.Focus();
        return (window, editor);
    }

    [AvaloniaFact]
    public async Task CtrlC_WithNoSelection_Should_CopyTheWholeCurrentLine_IncludingItsLineEnding()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2; // inside "line2", nothing selected
        Assert.Equal(0, editor.SelectionLength);

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        var copied = await TopLevel.GetTopLevel(editor)!.Clipboard!.TryGetValueAsync(DataFormat.Text);
        Assert.Equal("line2" + Environment.NewLine, copied);
    }

    [AvaloniaFact]
    public async Task CtrlX_WithNoSelection_Should_CutTheWholeCurrentLine_IncludingItsLineEnding()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2;
        Assert.Equal(0, editor.SelectionLength);

        window.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, null);

        var copied = await TopLevel.GetTopLevel(editor)!.Clipboard!.TryGetValueAsync(DataFormat.Text);
        Assert.Equal("line2" + Environment.NewLine, copied);
        Assert.Equal("line1\nline3", editor.Text);
    }

    [AvaloniaFact]
    public async Task CtrlC_WithAnActualSelection_Should_StillOnlyCopyTheSelection_NotTheWholeLine()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.Select(editor.Document.GetLineByNumber(2).Offset, 4); // "line" out of "line2"

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        var copied = await TopLevel.GetTopLevel(editor)!.Clipboard!.TryGetValueAsync(DataFormat.Text);
        Assert.Equal("line", copied);
    }
}
