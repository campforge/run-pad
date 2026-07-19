using DotPad.Format.RPad;

namespace DotPad.Services;

public interface IWindowService
{
    // Takes a live stream rather than a snapshot so the dialog picks up front matter that becomes
    // available after it's already open (see EditorTabItemViewModel.FrontMatter).
    Task ShowDependenciesAsync(IObservable<RpadFrontMatter?>? frontMatter = null);
    Task ShowPreferencesAsync();
    Task ShowErrorAsync(string title, string message);
    Task ShowAboutAsync();

    /// <summary>
    /// Shows a native Save As dialog defaulting to <paramref name="startFolder"/> (the user can still
    /// navigate into any subfolder from there — MainWindowViewModel.SaveTabAsAsync's own defaulting
    /// stops there, the OS-native picker handles subfolder navigation for free). Returns the chosen
    /// local file path, or null if the dialog was cancelled or unavailable.
    /// </summary>
    Task<string?> ShowSaveFileDialogAsync(string suggestedFileName, string startFolder);

    /// <summary>
    /// Shows a native Open dialog filtered to <c>.linq</c> files, defaulting to
    /// <paramref name="startFolder"/> (TODO.md #63, "File → Import LINQPad Query..."). Returns the
    /// chosen local file path, or null if the dialog was cancelled or unavailable.
    /// </summary>
    Task<string?> ShowOpenLinqFileDialogAsync(string startFolder);

    /// <summary>
    /// Asks whether to save a tab's unsaved changes before discarding them (closing an untitled tab
    /// with real typed content — see EditorTabsControl.RequestCloseTabAsync). Closing the dialog
    /// without picking a button (e.g. the window's own [X], or a null owner window meaning it
    /// couldn't even be shown) is treated as Cancel — the safe default when nothing else is known.
    /// </summary>
    Task<UnsavedChangesResult> ShowUnsavedChangesDialogAsync(string fileName);
}
