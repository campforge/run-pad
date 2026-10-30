using Invoklet.UserControls.Models;

namespace Invoklet.Services;

/// <summary>
/// What happens to the unsaved text of tabs when something is about to discard it: a tab is closed, the app is
/// closed with Save All, a file operation is about to touch the files. Saving is asked of the caller, which knows
/// the editor and the dialogs about a file changed on disk; the questions to the user go through the windows.
/// </summary>
internal sealed class UnsavedChangesFlow(
    IWindowService windows,
    Func<EditorTabItemViewModel, Task<InPlaceSaveResult>> saveInPlace,
    Func<EditorTabItemViewModel, Task> saveAs)
{
    /// <summary>
    /// Whether a tab may be closed. A clean tab may. A named tab is written in place; when that fails the error is shown
    /// and the user gets the same Save (As) / Don't Save / Cancel choice an untitled tab gets, otherwise a file that can
    /// never be written (read-only, gone, disk full) would make its tab impossible to close. Save As only clears the
    /// dirty mark on success, so a tab still dirty afterwards means the user changed their mind, not that content was lost.
    /// </summary>
    public async Task<bool> ResolveBeforeCloseAsync(EditorTabItemViewModel tab)
    {
        if (!tab.IsDirty) return true;

        if (tab.FilePath is not null)
        {
            // A declined save (the file changed on disk and the user cancelled) stops the close too.
            var result = await saveInPlace(tab);
            if (result.Status == InPlaceSaveStatus.Saved) return true;
            if (result.Status == InPlaceSaveStatus.Cancelled) return false;
            await windows.ShowErrorAsync("Save Failed", result.Error);
        }

        var choice = await windows.ShowUnsavedChangesDialogAsync(tab.FileName);
        if (choice == UnsavedChangesResult.Cancel) return false;
        if (choice == UnsavedChangesResult.DontSave) return true;

        await saveAs(tab);
        return !tab.IsDirty;
    }

    /// <summary>
    /// Save All on exit: every still dirty and still open tab, named ones in place and untitled ones through Save As. Any
    /// cancel or failure stops at once and returns false, without undoing the saves already made.
    /// </summary>
    public async Task<bool> SaveAllAsync(IEnumerable<EditorTabItemViewModel> tabs, Func<EditorTabItemViewModel, bool> isOpen)
    {
        foreach (var tab in tabs)
        {
            if (!tab.IsDirty || !isOpen(tab)) continue;
            if (tab.FilePath is not null)
            {
                var result = await saveInPlace(tab);
                if (result.Status == InPlaceSaveStatus.Cancelled) return false;
                if (result.Status == InPlaceSaveStatus.Failed)
                {
                    await windows.ShowErrorAsync("Save Failed", result.Error);
                    return false;
                }
            }
            else
            {
                await saveAs(tab);
                if (tab.IsDirty) return false; // Save As was cancelled or failed
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the affected tabs before a file operation. Stops at the first that cannot be written, or whose file the
    /// user declined to replace after it changed on disk: nothing is closed and nothing on disk touched then.
    /// </summary>
    public async Task<bool> SaveBeforeFileOperationAsync(IEnumerable<EditorTabItemViewModel> tabs, string errorTitle)
    {
        foreach (var tab in tabs)
        {
            var result = await saveInPlace(tab);
            if (result.Status == InPlaceSaveStatus.Saved) continue;
            if (result.Status == InPlaceSaveStatus.Failed) _ = windows.ShowErrorAsync(errorTitle, result.Error);
            return false;
        }

        return true;
    }
}
