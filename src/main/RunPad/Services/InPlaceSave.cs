using RunPad.UserControls.Models;

namespace RunPad.Services;

/// <summary>How a save of a tab to its own file ended.</summary>
public enum InPlaceSaveStatus
{
    Saved,
    Cancelled,
    Failed,
}

/// <param name="Error">For <see cref="InPlaceSaveStatus.Failed"/>: why the file couldn't be written.</param>
public sealed record InPlaceSaveResult(InPlaceSaveStatus Status, string Error = "")
{
    public static InPlaceSaveResult Saved { get; } = new(InPlaceSaveStatus.Saved);
    public static InPlaceSaveResult Cancelled { get; } = new(InPlaceSaveStatus.Cancelled);
}

/// <summary>
/// Writes a tab to its own file — the one path every in-place save takes: Ctrl+S, front-matter
/// edits, closing, quitting and saving before a rename, move or delete. A file that changed since
/// the tab last read or wrote it is never replaced without asking: the user saves under another
/// name, overwrites, or cancels, and a cancelled save leaves the file as it is and the tab unsaved.
/// </summary>
/// <remarks>
/// The check runs on the calling thread right before the write, so the time another program has to
/// change the file in between is as short as the write path allows; it is not a lock, and a write
/// landing exactly in that gap is not detected. One save per tab runs at a time: a second request
/// while the first waits for the user gets that first save's result instead of a second dialog.
/// </remarks>
public static class InPlaceSave
{
    public static Task<InPlaceSaveResult> SaveAsync(
        EditorTabItemViewModel tab, IWindowService windows, Func<Task> saveAs)
    {
        if (tab.PendingSave is { IsCompleted: false } pending) return pending;
        var save = CheckAndWriteAsync(tab, windows, saveAs);
        tab.PendingSave = save.IsCompleted ? null : save;
        return save;
    }

    /// <summary>
    /// The same check and write without joining a save in progress — for Save As choosing the tab's
    /// own file again while the conflict dialog's Save As is what is running.
    /// </summary>
    public static async Task<InPlaceSaveResult> CheckAndWriteAsync(
        EditorTabItemViewModel tab, IWindowService windows, Func<Task> saveAs)
    {
        if (tab.FilePath is not { } path) return InPlaceSaveResult.Cancelled;
        if (tab.WriteToFile is not { } write)
            return new(InPlaceSaveStatus.Failed, $"Could not save '{Path.GetFileName(path)}': the editor is not ready.");

        try
        {
            if ((tab.RequiresBaselineConfirmation && File.Exists(path)) || tab.DiskState?.ConflictsWith(path) == true)
            {
                // From here on the tab's text differs from the file, whatever it showed before.
                tab.IsDirty = true;
                switch (await windows.ShowSaveConflictDialogAsync(Path.GetFileName(path)))
                {
                    case SaveConflictChoice.SaveAs:
                        await saveAs();
                        return tab.IsDirty ? InPlaceSaveResult.Cancelled : InPlaceSaveResult.Saved;
                    case SaveConflictChoice.Overwrite:
                        break;
                    default:
                        return InPlaceSaveResult.Cancelled;
                }
                // The choice was made for this tab's file; it may have moved on with Save As meanwhile.
                if (tab.FilePath != path) return InPlaceSaveResult.Cancelled;
            }

            write(path);
            tab.AcceptWrittenState(path);
            tab.IsDirty = false;
            return InPlaceSaveResult.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(InPlaceSaveStatus.Failed, $"Could not save '{Path.GetFileName(path)}':\n{ex.Message}");
        }
    }
}
