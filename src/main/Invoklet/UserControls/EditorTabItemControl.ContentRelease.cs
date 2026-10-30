using Avalonia.Threading;
using AvaloniaEdit.Document;
using Invoklet.Services;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging;

namespace Invoklet.UserControls;

// The heavy part of a tab, its text and its output, can wait on disk while the control stays as it is, so a
// tab that is not being used costs little. The control, the IntelliSense service and the metadata remain.
public partial class EditorsTabItemControl
{
    private bool _contentReleased;
    private Task<bool>? _restoring;

    internal ITabSpillStore? SpillStore { get; set; }

    internal bool IsContentReleased => _contentReleased;

    internal bool TryReleaseContent()
    {
        if (_disposed || _contentReleased || HasRunningProcess || SpillStore is not { } store) return false;
        var editor = State.Editor;
        // The output of a run waits in the folder of that run; only an output without one goes to the spill.
        var spilledOutput = SaveRunOutput() ? null : CaptureOutput();
        if (!store.TryWrite(_vm.RecoveryId, new ReleasedContent(editor.Text, CaptureViewState(), spilledOutput)))
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

    /// <summary>
    /// Brings the text and the output back. The file is read off the UI thread, and the editor stays
    /// invisible until it is filled, so neither a freeze nor an empty editor is seen. Asked again while it runs,
    /// it is the same restore.
    /// </summary>
    internal Task<bool> TryRestoreContentAsync()
    {
        if (_disposed || !_contentReleased) return Task.FromResult(true);
        if (_restoring is { } running) return running;
        var restoring = RestoreContentAsync();
        if (!restoring.IsCompleted) _restoring = restoring;
        return restoring;
    }

    private async Task<bool> RestoreContentAsync()
    {
        var editor = State.Editor;
        editor.Opacity = 0;
        try
        {
            var store = SpillStore;
            var id = _vm.RecoveryId;
            var content = store is null ? null : await Task.Run(() => store.TryRead(id));
            if (_disposed) return false;
            if (!_contentReleased) return true;
            if (content is null) return false;

            var dirty = _vm.IsDirty;
            // A script that was saved is read again, so a change made to its file meanwhile is not hidden.
            var saved = !dirty && FrontMatterSnapshot() == _metadataBaseline && _vm.FilePath is not null;
            // From here the handlers see the text come back, as they would see a typed one.
            _contentReleased = false;
            editor.IsReadOnly = false;
            editor.Document = new TextDocument(content.Text);
            if (saved)
                KeepTextIfFileIsGone(ReloadFromDisk());
            else
                _vm.IsDirty = dirty;

            RestoreOutput(content.Output ?? (State.OutputDirectory is { } folder ? RunOutputFile.TryLoad(folder) : null));
            RestoreViewState(content.Editor);
            return true;
        }
        finally
        {
            _restoring = null;
            if (_contentReleased) editor.Opacity = 1; // not restored: the empty read-only editor, as the error says
        }
    }

    // The file of a saved script was read again and could not give a script of this tab's language: what
    // the tab showed stays on screen, and it no longer matches a file, so it is unsaved.
    private void KeepTextIfFileIsGone(DiskReload result)
    {
        if (result == DiskReload.Reloaded) return;
        _vm.IsDirty = true;
        SetStatus(result == DiskReload.OtherLanguage
            ? "The file is now in another language; showing the text from before"
            : "The file could not be read; showing the text from before", "Invoklet.Status.ErrorBrush");
        UpdateOutputUi();
    }

    // The output of the last run, beside the library it ran: written when a run ends, and when the content
    // of the tab is released. False when the tab has no such folder yet.
    private bool SaveRunOutput()
    {
        if (State.OutputDirectory is not { } folder) return false;
        try
        {
            RunOutputFile.Save(folder, CaptureOutput());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runLogger.LogWarning("Could not keep the output of a run ({ErrorType})", ex.GetType().Name);
            return false;
        }
    }

    // The tab moved to another file (Save As, a rename): its runs are built, and their output kept, in the
    // folder of that file from now on. The folder of the old file keeps the output of the runs it saw, so
    // opening that file again does not show the output of runs of another one.
    private void RebindRunFolder(string newPath)
    {
        if (_buildCache.EnsureOutputDirectory(newPath, EnsureWorkDirectory()) is not { } folder) return;
        var old = State.OutputDirectory;
        State.OutputDirectory = folder;
        if (!_contentReleased)
            SaveRunOutput(); // what the tab shows now belongs to the new file
        else if (old is not null && RunOutputFile.TryLoad(old) is { } waiting)
            SaveRunOutputOf(folder, waiting); // the output is still waiting in the old folder: it moves along
    }

    private void SaveRunOutputOf(string folder, TabOutputState output)
    {
        try
        {
            RunOutputFile.Save(folder, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runLogger.LogWarning("Could not keep the output of a run ({ErrorType})", ex.GetType().Name);
        }
    }

    // Shows the output of the last run the script's folder holds, for a tab opened again.
    private void LoadRunOutput()
    {
        if (State.Output.Content.Length > 0 || State.OutputDirectory is not { } folder) return;
        if (RunOutputFile.TryLoad(folder) is { } output) RestoreOutput(output);
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
        editor.Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            editor.UpdateLayout();
            ScrollEditorToLine(state.TopLine);
            editor.Opacity = 1;
        }, DispatcherPriority.Loaded);
    }

    private TabOutputState CaptureOutput()
        => new(State.Output.Content, State.HadRunError, new Dictionary<string, string>(State.DumpContainerBlocks));

    private void RestoreOutput(TabOutputState? output)
    {
        if (output is null) return;
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
