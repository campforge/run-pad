using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeEditor;
// CodeTextEditor's own half of F2 rename is just "recognize
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

    // Same hand-off shape as F2 rename, mirrored here rather than
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

    // Same hand-off shape again, plain F12
    // this time (Shift+F12 stays "Find All References", see above).
    [AvaloniaFact]
    public void PressingPlainF12_Should_RaiseGoToDefinitionRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.GoToDefinitionRequested += (_, _) => raised++;

        window.KeyPress(Key.F12, RawInputModifiers.None, PhysicalKey.F12, null);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void PressingShiftF12_Should_NotRaiseGoToDefinitionRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.GoToDefinitionRequested += (_, _) => raised++;

        window.KeyPress(Key.F12, RawInputModifiers.Shift, PhysicalKey.F12, null);

        Assert.Equal(0, raised);
    }

    // Ctrl+F12 was briefly bound to a separate "Go to Implementation" request, then
    // dropped (user feedback 2026-07-19: modifier+F-key combos collide with desktop-environment
    // global shortcuts often enough on Linux, e.g. KDE Plasma, that it isn't a safe default). Plain
    // F12 (EditorTabItemControl.ShowGoToDefinitionAsync) now covers both by checking implementations
    // first — this guards against silently re-adding an intercepted Ctrl+F12 binding.
    [AvaloniaFact]
    public void PressingCtrlF12_Should_NotRaiseGoToDefinitionRequested()
    {
        var (window, editor) = CreateEditor("value");
        var raised = 0;
        editor.GoToDefinitionRequested += (_, _) => raised++;

        window.KeyPress(Key.F12, RawInputModifiers.Control, PhysicalKey.F12, null);

        Assert.Equal(0, raised);
    }
}
