using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Live user report ("if you press ctrl+c with no characters selected, the whole line gets copied,
// including the line break" / same for Ctrl+X): the forked AvaloniaEdit's EditingCommandHandler already implements
// this exact behavior (OnCopy/OnCut fall back to CopyWholeLine when TextArea.Selection.IsEmpty), gated
// behind TextEditorOptions.CutCopyWholeLine, whose class-level default is already `true` — and RunPad's
// CodeTextEditor never overrides it. Nothing here was RunPad-specific code to write; these tests exist
// to empirically confirm the already-shipped-upstream behavior actually reaches a real RunPad editor
// end-to-end (real Ctrl+C/Ctrl+X keypress through Avalonia.Headless, not just calling the command
// method directly), since neither this behavior nor Options.CutCopyWholeLine had any test coverage
// before, upstream (AvaloniaEdit.Tests) or in RunPad.
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

    // AvaloniaEdit's ApplicationCommands.Copy/Cut gestures use Meta (Cmd) on macOS and Control elsewhere
    // (AvaloniaEditCommands.GetPlatformCommandKey); a hardcoded Control+C never reaches OnCopy on macOS.
    private static readonly RawInputModifiers CommandModifier =
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // CI failure on macOS (Linux/dev machine never showed it): EditingCommandHandler.SetClipboardText
    // (the vendored AvaloniaEdit fork, not RunPad's own code) calls Clipboard.SetDataAsync(text) and
    // discards the returned Task — fire-and-forget, by design there (it's called from a synchronous
    // command handler). Reading the clipboard back immediately after KeyPress returns races that
    // still-in-flight write; apparently fast enough to always win on Linux, not on (slower/differently
    // scheduled) macOS CI. Same short-poll shape as DapSessionRunningBreakpointResyncTests'
    // WaitForWrittenRequestAsync for the same class of "fire-and-forget completes asynchronously"
    // problem elsewhere in this suite.
    private static async Task<string?> WaitForClipboardTextAsync(TopLevel topLevel)
    {
        for (var i = 0; i < 100; i++)
        {
            var data = await topLevel.Clipboard!.TryGetDataAsync();
            if (data is null)
            {
                await Task.Delay(20);
                continue;
            }

            var text = await data.TryGetTextAsync();
            if (text is not null) return text;
            await Task.Delay(20);
        }

        return null;
    }

    [AvaloniaFact]
    public async Task CtrlC_WithNoSelection_Should_CopyTheWholeCurrentLine_IncludingItsLineEnding()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2; // inside "line2", nothing selected
        Assert.Equal(0, editor.SelectionLength);

        window.KeyPress(Key.C, CommandModifier, PhysicalKey.C, null);

        var copied = await WaitForClipboardTextAsync(TopLevel.GetTopLevel(editor)!);
        Assert.Equal("line2" + Environment.NewLine, copied);
    }

    [AvaloniaFact]
    public async Task CtrlX_WithNoSelection_Should_CutTheWholeCurrentLine_IncludingItsLineEnding()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2;
        Assert.Equal(0, editor.SelectionLength);

        window.KeyPress(Key.X, CommandModifier, PhysicalKey.X, null);

        var copied = await WaitForClipboardTextAsync(TopLevel.GetTopLevel(editor)!);
        Assert.Equal("line2" + Environment.NewLine, copied);
        Assert.Equal("line1\nline3", editor.Text);
    }

    [AvaloniaFact]
    public async Task CtrlC_WithAnActualSelection_Should_StillOnlyCopyTheSelection_NotTheWholeLine()
    {
        var (window, editor) = CreateEditorWithText("line1\nline2\nline3");
        editor.Select(editor.Document.GetLineByNumber(2).Offset, 4); // "line" out of "line2"

        window.KeyPress(Key.C, CommandModifier, PhysicalKey.C, null);

        var copied = await WaitForClipboardTextAsync(TopLevel.GetTopLevel(editor)!);
        Assert.Equal("line", copied);
    }
}
