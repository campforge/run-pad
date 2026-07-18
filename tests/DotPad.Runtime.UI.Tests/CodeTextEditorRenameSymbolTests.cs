using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "умное переименование" — CodeTextEditor's own half of F2 rename is just "recognize
// the keypress and raise RenameSymbolRequested"; the host (EditorTabItemControl) resolves what's
// renameable and drives the actual dialog/edit (see EditorTabItemControlRenameEditsTests.cs for
// that part, and CSharpIntelliSenseRenameTests.cs for symbol resolution) — this only needs to prove
// the keypress itself is recognized, and specifically that it doesn't fire for anything but a bare
// F2 (e.g. Shift+F2, which AvaloniaEdit/this control don't assign any meaning to, must pass through
// untouched rather than being swallowed).
public class CodeTextEditorRenameSymbolTests
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
    public void PressingF2_Should_RaiseRenameSymbolRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.RenameSymbolRequested += (_, _) => raised++;

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void PressingShiftF2_Should_NotRaiseRenameSymbolRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.RenameSymbolRequested += (_, _) => raised++;

        window.KeyPress(Key.F2, RawInputModifiers.Shift, PhysicalKey.F2, null);

        Assert.Equal(0, raised);
    }

    // TODO.md #62 "find references" — same hand-off shape as F2 rename, mirrored here rather than
    // in its own file since it's the same control/pattern (Shift+F12, not F2).
    [AvaloniaFact]
    public void PressingShiftF12_Should_RaiseFindReferencesRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.FindReferencesRequested += (_, _) => raised++;

        window.KeyPress(Key.F12, RawInputModifiers.Shift, PhysicalKey.F12, null);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void PressingPlainF12_Should_NotRaiseFindReferencesRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.FindReferencesRequested += (_, _) => raised++;

        window.KeyPress(Key.F12, RawInputModifiers.None, PhysicalKey.F12, null);

        Assert.Equal(0, raised);
    }
}
