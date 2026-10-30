using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.UserControls;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

public sealed class EditorInteractionSubscriptionsTests
{
    [AvaloniaFact]
    public void Attach_IsIdempotent_AndDisposeDetachesCaretHandler()
    {
        var editor = new CodeTextEditor { Document = new TextDocument("abc") };
        var caretChanges = 0;
        var owner = new EditorInteractionSubscriptions(
            editor,
            (_, _) => { },
            (_, _) => caretChanges++,
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            _ => { },
            (_, _) => { },
            (_, _) => { },
            (_, _) => { });

        owner.Attach();
        owner.Attach();
        editor.TextArea.Caret.Offset = 1;
        Assert.Equal(1, caretChanges);

        owner.Dispose();
        owner.Dispose();
        editor.TextArea.Caret.Offset = 2;

        Assert.Equal(1, caretChanges);
    }

    [AvaloniaFact]
    public void Attach_AllEditorEventsAreSingleUse_AndDisposeDetachesThem()
    {
        var editor = new CodeTextEditor { Document = new TextDocument("value") };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        var counts = new int[8];
        var owner = new EditorInteractionSubscriptions(
            editor,
            (_, _) => counts[0]++,
            (_, _) => { },
            (_, _) => counts[1]++,
            (_, _) => counts[2]++,
            (_, _) => counts[3]++,
            (_, _) => counts[4]++,
            _ => { },
            (_, _) => counts[5]++,
            (_, _) => counts[6]++,
            (_, _) => counts[7]++);

        owner.Attach();
        owner.Attach();
        editor.Text = "changed";
        editor.Document = new TextDocument("replacement");
        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        window.KeyPress(Key.F12, RawInputModifiers.Shift, PhysicalKey.F12, null);
        window.KeyPress(Key.F12, RawInputModifiers.None, PhysicalKey.F12, null);
        editor.AddBreakpoint(1);
        var breakpoint = editor.Breakpoints.Single();
        editor.RemoveBreakpoint(breakpoint);
        editor.RaiseBreakpointRightClicked(1, null);

        Assert.Equal(new[] { 2, 1, 1, 1, 1, 1, 1, 1 }, counts);

        owner.Dispose();
        owner.Dispose();
        editor.Text = "after dispose";
        editor.Document = new TextDocument("after dispose document");
        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        window.KeyPress(Key.F12, RawInputModifiers.Shift, PhysicalKey.F12, null);
        window.KeyPress(Key.F12, RawInputModifiers.None, PhysicalKey.F12, null);
        editor.AddBreakpoint(2);
        editor.RemoveBreakpoint(editor.Breakpoints.Single());
        editor.RaiseBreakpointRightClicked(2, null);

        Assert.Equal(new[] { 2, 1, 1, 1, 1, 1, 1, 1 }, counts);
    }
}
