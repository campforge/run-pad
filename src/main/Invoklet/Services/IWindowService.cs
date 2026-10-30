using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Services;

public interface IWindowService
{
    // Takes a live stream rather than a snapshot so the dialog picks up front matter that becomes
    // available after it's already open (see EditorTabItemViewModel.FrontMatter).
    Task ShowDependenciesAsync(IObservable<MdRunFrontMatter?>? frontMatter = null, IObservable<string?>? scriptPath = null);

    /// <summary>Asks whether to add a connection together with the NuGet packages its driver needs; false when declined or no window is open.</summary>
    Task<bool> ConfirmConnectionPackagesAsync(string message);
    Task ShowPreferencesAsync();
    Task ShowErrorAsync(string title, string message);
    Task ShowAboutAsync();

    /// <summary>Tools → Secret Store: the user's named secrets for <c>Util.GetSecret</c>.</summary>
    Task ShowSecretStoreAsync();

    /// <summary>Tools → Connections: the user's own database, HTTP and gRPC connections.</summary>
    /// <summary>Opens the Connections editor, with the named connection selected when it is in the list.</summary>
    Task ShowConnectionsAsync(string? connectionName = null);

    /// <summary>
    /// Shows a native Save As dialog defaulting to <paramref name="startFolder"/> (the user can still
    /// navigate into any subfolder from there — MainWindowViewModel.SaveTabAsAsync's own defaulting
    /// stops there, the OS-native picker handles subfolder navigation for free). Returns the chosen
    /// local file path, or null if the dialog was cancelled or unavailable.
    /// </summary>
    Task<string?> ShowSaveFileDialogAsync(string suggestedFileName, string startFolder);

    /// <summary>Native picker for a local .inv script; null on cancellation.</summary>
    Task<string?> ShowOpenScriptFileDialogAsync(string startFolder);

    /// <summary>
    /// Shows a native Open dialog filtered to <c>.linq</c> files, defaulting to
    /// <paramref name="startFolder"/> ("File → Import LINQPad Query..."). Returns the
    /// chosen local file path, or null if the dialog was cancelled or unavailable.
    /// </summary>
    Task<string?> ShowOpenLinqFileDialogAsync(string startFolder);

    /// <summary>
    /// Shows a native Open dialog filtered to <c>.dll</c> files, defaulting to
    /// <paramref name="startFolder"/> (ILSpy-style assembly explorer). Returns the chosen local file path, or null if
    /// the dialog was cancelled or
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
    /// Opens <paramref name="filePath"/> in its own text editor window —
    /// or brings the window already editing that file to the front. A file
    /// that can't be read shows an error instead.
    /// </summary>
    Task ShowTextEditorAsync(string filePath);

    /// <summary>
    /// Shows the non-modal Assembly Explorer window — lets
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

    /// <summary>
    /// Asks what to do when saving would replace a file changed by another program since Invoklet
    /// last read or wrote it. Closing the dialog, or no window to show it on, is Cancel.
    /// </summary>
    Task<SaveConflictChoice> ShowSaveConflictDialogAsync(string fileName);

    /// <summary>
    /// Asks what to do with the unsaved tabs named when the app is closed while draft recovery is on.
    /// Closing the dialog, or no window to show it on, is Cancel.
    /// </summary>
    Task<ExitUnsavedChoice> ShowExitUnsavedDialogAsync(IReadOnlyList<string> fileNames);

    /// <summary>
    /// Asks whether a tab with unsaved changes should reload its file, which another program
    /// changed. True reloads; keeping the changes, closing the dialog or no window to show it on is false.
    /// </summary>
    Task<bool> ShowReloadChangedFileDialogAsync(string fileName);

    /// <summary>
    /// Asks before File → Revert to Saved drops a tab's unsaved changes. True reverts; Cancel,
    /// closing the dialog or no window to show it on is false.
    /// </summary>
    Task<bool> ShowRevertFileDialogAsync(string fileName);
}
