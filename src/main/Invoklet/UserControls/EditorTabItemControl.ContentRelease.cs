using Avalonia.Threading;
using AvaloniaEdit.Document;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

// The heavy part of a tab, its text and its output, can wait on disk while the control stays as it is, so a
// tab that is not being used costs little. The control, the IntelliSense service and the metadata remain.
public partial class EditorsTabItemControl
{
    private bool _contentReleased;

    internal ITabSpillStore? SpillStore { get; set; }

    internal bool IsContentReleased => _contentReleased;

    internal bool TryReleaseContent()
    {
        if (_disposed || _contentReleased || HasRunningProcess || SpillStore is not { } store) return false;
        var editor = State.Editor;
        if (!store.TryWrite(_vm.RecoveryId, new ReleasedContent(editor.Text, CaptureViewState(), CaptureOutput())))
            return false;

        var dirty = _vm.IsDirty;
        // Set first: the IntelliSense service must not be told the script became empty.
        _contentReleased = true;
        // A new document frees the text, its undo history and the highlighting tokens of every line.
        editor.Document = new TextDocument();
        editor.IsReadOnly = true;
        State.Output.Clear();
        State.DumpContainerBlocks.Clear();
        _vm.IsDirty = dirty;
        return true;
    }

    internal bool TryRestoreContent()
    {
        if (_disposed || !_contentReleased) return true;
        if (SpillStore?.TryRead(_vm.RecoveryId) is not { } content) return false;

        var editor = State.Editor;
        var dirty = _vm.IsDirty;
        // A script that was saved is read again, so a change made to its file meanwhile is not hidden.
        var saved = !dirty && FrontMatterSnapshot() == _metadataBaseline && _vm.FilePath is not null;
        // From here the handlers see the text come back, as they would see a typed one.
        _contentReleased = false;
        editor.IsReadOnly = false;
        editor.Document = new TextDocument(content.Text);
        if (saved) ReloadFromDisk();
        else _vm.IsDirty = dirty;

        RestoreOutput(content.Output);
        RestoreViewState(content.Editor);
        return true;
    }

    private EditorViewState CaptureViewState()
    {
        var editor = State.Editor;
        return new EditorViewState(editor.CaretOffset, editor.SelectionStart, editor.SelectionLength, EditorTopLine());
    }

    private void RestoreViewState(EditorViewState state)
    {
        var editor = State.Editor;
        var length = editor.Document.TextLength;
        editor.CaretOffset = Math.Min(state.CaretOffset, length);
        var start = Math.Min(state.SelectionStart, length);
        if (state.SelectionLength > 0) editor.Select(start, Math.Min(state.SelectionLength, length - start));

        // The editor may have no size yet, and a line can only be scrolled to once it has one. It stays
        // invisible until then, so the text is never seen at the top and then jumping.
        Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            editor.UpdateLayout();
            ScrollEditorToLine(state.TopLine);
            Opacity = 1;
        }, DispatcherPriority.Loaded);
    }

    private TabOutputState CaptureOutput()
        => new(State.Output.Content, State.HadRunError, new Dictionary<string, string>(State.DumpContainerBlocks));

    private void RestoreOutput(TabOutputState output)
    {
        State.Output.Clear();
        State.Output.Append(output.Content);
        State.HadRunError = output.HadRunError;
        State.DumpContainerBlocks.Clear();
        foreach (var (id, block) in output.DumpContainerBlocks) State.DumpContainerBlocks[id] = block;
    }

    // The text and the caret for the session checkpoint: while the content is on disk, from there. A
    // checkpoint must never take the empty editor for the script, so a spill that cannot be read fails it.
    private (string Text, int Caret, int SelectionStart, int SelectionLength) TextForRecovery()
    {
        var editor = State.Editor;
        if (!_contentReleased) return (editor.Text, editor.CaretOffset, editor.SelectionStart, editor.SelectionLength);
        if (SpillStore?.TryRead(_vm.RecoveryId) is not { } content)
            throw new InvalidOperationException("The text of a tab that is not loaded could not be read.");
        return (content.Text, content.Editor.CaretOffset, content.Editor.SelectionStart, content.Editor.SelectionLength);
    }
}
