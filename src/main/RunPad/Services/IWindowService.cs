using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;

namespace RunPad.Services;

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
    /// Shows a native Open dialog filtered to <c>.dll</c> files, defaulting to
    /// <paramref name="startFolder"/> (TODO.md #62, ILSpy-style assembly explorer, user request
    /// 2026-07-19). Returns the chosen local file path, or null if the dialog was cancelled or
    /// unavailable.
    /// </summary>
    Task<string?> ShowOpenAssemblyFileDialogAsync(string startFolder);

    /// <summary>
    /// Shows a native Save As dialog for decompiled code (the assembly explorer's "Save Code..."):
    /// a C# (<c>.cs</c>) or IL (<c>.il</c>) file per <paramref name="extension"/>, defaulting to
    /// <paramref name="startFolder"/>. Returns the chosen local file path, or null if the dialog was
    /// cancelled or unavailable.
    /// </summary>
    Task<string?> ShowSaveCodeFileDialogAsync(string suggestedFileName, string extension, string startFolder);

    /// <summary>Native Open dialog for any file (the text editor window's "Open Text File..."); null if cancelled.</summary>
    Task<string?> ShowOpenTextFileDialogAsync(string startFolder);

    /// <summary>Native Save As dialog for any file (the text editor window's Save As); null if cancelled.</summary>
    Task<string?> ShowSaveTextFileDialogAsync(string suggestedFileName, string startFolder);

    /// <summary>
    /// Opens <paramref name="filePath"/> in its own text editor window (TODO.md "Отдельное окно
    /// текстового редактора") — or brings the window already editing that file to the front. A file
    /// that can't be read shows an error instead.
    /// </summary>
    Task ShowTextEditorAsync(string filePath);

    /// <summary>
    /// Shows the non-modal Assembly Explorer window (TODO.md #62, user request 2026-07-19) — lets
    /// the script keep running/editable behind it. There's one per app: an open one is brought to
    /// the front instead of opening another.
    /// </summary>
    Task ShowAssemblyExplorerAsync(string startFolder);

    /// <summary>
    /// Go to Definition (F12) from the user's script landing outside it: shows
    /// <paramref name="decompiled"/> in the Assembly Explorer with <paramref name="definition"/>'s row
    /// selected in its tree — in a new tab of the open explorer, or in a new explorer if none is open.
    /// </summary>
    Task ShowDefinitionInAssemblyExplorerAsync(DecompiledMemberResult decompiled, IDefinitionResult definition);

    /// <summary>
    /// Asks whether to save a tab's unsaved changes before discarding them (closing an untitled tab
    /// with real typed content — see EditorTabsControl.RequestCloseTabAsync). Closing the dialog
    /// without picking a button (e.g. the window's own [X], or a null owner window meaning it
    /// couldn't even be shown) is treated as Cancel — the safe default when nothing else is known.
    /// </summary>
    Task<UnsavedChangesResult> ShowUnsavedChangesDialogAsync(string fileName);
}
