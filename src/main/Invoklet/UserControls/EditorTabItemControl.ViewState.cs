using Avalonia.Threading;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

public partial class EditorsTabItemControl
{
    internal EditorViewState CaptureViewState()
    {
        var editor = State.Editor;
        return new EditorViewState(
            editor.CaretOffset, editor.SelectionStart, editor.SelectionLength, EditorTopLine(),
            editor.Breakpoints.Select(b => new BreakpointState(b.Line, b.Condition, b.HitCondition, b.IsEnabled)).ToList());
    }

    internal void RestoreViewState(EditorViewState state)
    {
        var editor = State.Editor;
        foreach (var saved in state.Breakpoints)
        {
            if (saved.Line < 1 || saved.Line > editor.Document.LineCount) continue;
            editor.AddBreakpoint(saved.Line);
            var added = editor.Breakpoints.First(b => b.Line == saved.Line);
            added.Condition = saved.Condition;
            added.HitCondition = saved.HitCondition;
            added.IsEnabled = saved.IsEnabled;
        }
        editor.InvalidateBreakpoints();

        var length = editor.Document.TextLength;
        editor.CaretOffset = Math.Min(state.CaretOffset, length);
        var start = Math.Min(state.SelectionStart, length);
        if (state.SelectionLength > 0) editor.Select(start, Math.Min(state.SelectionLength, length - start));

        // The editor has no size until the tab is shown, and a line can only be scrolled to once it has one.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            editor.UpdateLayout();
            ScrollEditorToLine(state.TopLine);
        }, DispatcherPriority.Loaded);
    }
}
