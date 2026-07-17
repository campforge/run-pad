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
}
