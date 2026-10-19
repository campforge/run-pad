using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;
using RunPad.Services.Preferences;
using RunPad.Windows;
using RunPad.Windows.Models;

namespace RunPad.Services;

// Used to take the whole IServiceProvider and resolve dependencies inside each method
// (Service Locator — DIP violation, constructor didn't document real dependencies, hard to test in
// isolation). Explicit Func<TWindow> factories instead, registered in App.axaml.cs's ConfigureServices.
// DependenciesWindow/PreferencesWindow are DI-registered as Transient (a fresh instance each time
// they're shown), so a Func<T> factory — not a direct T — is the right shape for both.
public sealed class WindowService : IWindowService
{
    private readonly Func<DependenciesWindow> _dependenciesWindowFactory;
    private readonly Func<PreferencesWindow> _preferencesWindowFactory;
    private readonly IThemeService _themeService;
    private readonly IPreferencesService? _preferences;

    // preferences: the text editor window's Save/Save As key gestures (the user's keyboard settings);
    // optional so tests constructing a WindowService for other windows needn't supply it.
    public WindowService(
        Func<DependenciesWindow> dependenciesWindowFactory,
        Func<PreferencesWindow> preferencesWindowFactory,
        IThemeService themeService,
        IPreferencesService? preferences = null)
    {
        _dependenciesWindowFactory = dependenciesWindowFactory;
        _preferencesWindowFactory = preferencesWindowFactory;
        _themeService = themeService;
        _preferences = preferences;
    }

    // The same lookup, repeated verbatim 6 times below.
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task ShowDependenciesAsync(IObservable<RpadFrontMatter?>? frontMatter = null)
    {
        var window = _dependenciesWindowFactory();
        using var subscription = frontMatter?.Subscribe(window.SetFrontMatter);
        await ShowDialogAsync(window);
    }

    public Task ShowPreferencesAsync()
        => ShowDialogAsync(_preferencesWindowFactory());

    public Task ShowErrorAsync(string title, string message)
        => ShowDialogAsync(new ErrorDialog(title, message));

    public Task ShowAboutAsync()
        => ShowDialogAsync(new AboutWindow());

    public async Task<string?> ShowSaveFileDialogAsync(string suggestedFileName, string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "rpad",
            SuggestedStartLocation = startLocation,
            FileTypeChoices = [new FilePickerFileType("RPad Script") { Patterns = ["*.rpad"] }],
        });
        return file is null ? null : file.TryGetLocalPath();
    }

    public async Task<string?> ShowOpenScriptFileDialogAsync(string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;
        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Script",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = [new FilePickerFileType("RPad Script") { Patterns = ["*.rpad"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> ShowOpenLinqFileDialogAsync(string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import .linq Query",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = [new FilePickerFileType(".linq Query") { Patterns = ["*.linq"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<UnsavedChangesResult> ShowUnsavedChangesDialogAsync(string fileName)
    {
        var owner = MainWindow;
        if (owner is null) return UnsavedChangesResult.Cancel;

        var result = await new UnsavedChangesDialog(fileName).ShowDialog<UnsavedChangesResult?>(owner);
        return result ?? UnsavedChangesResult.Cancel;
    }

    public async Task<ExitUnsavedChoice> ShowExitUnsavedDialogAsync(IReadOnlyList<string> fileNames)
    {
        var owner = MainWindow;
        if (owner is null) return ExitUnsavedChoice.Cancel;

        var result = await new ExitUnsavedDialog(fileNames).ShowDialog<ExitUnsavedChoice?>(owner);
        return result ?? ExitUnsavedChoice.Cancel;
    }

    public async Task<SaveConflictChoice> ShowSaveConflictDialogAsync(string fileName)
    {
        var owner = MainWindow;
        if (owner is null) return SaveConflictChoice.Cancel;

        var result = await new SaveConflictDialog(fileName).ShowDialog<SaveConflictChoice?>(owner);
        return result ?? SaveConflictChoice.Cancel;
    }

    public Task<bool> ShowReloadChangedFileDialogAsync(string fileName)
        => ShowReloadDialogAsync(ReloadFileDialog.ChangedOnDisk(fileName));

    public Task<bool> ShowRevertFileDialogAsync(string fileName)
        => ShowReloadDialogAsync(ReloadFileDialog.Revert(fileName));

    private async Task<bool> ShowReloadDialogAsync(ReloadFileDialog dialog)
    {
        var owner = MainWindow;
        if (owner is null) return false;
        return await dialog.ShowDialog<bool?>(owner) == true;
    }

    public async Task<string?> ShowOpenAssemblyFileDialogAsync(string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Browse Assembly",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = [new FilePickerFileType(".NET Assembly") { Patterns = ["*.dll"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> ShowOpenTextFileDialogAsync(string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Text File",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter =
            [
                new FilePickerFileType("Text and config files")
                {
                    Patterns = ["*.json", "*.yaml", "*.yml", "*.xml", "*.config", "*.ini", "*.toml", "*.txt", "*.md", "*.csv", "*.log"],
                },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> ShowSaveTextFileDialogAsync(string suggestedFileName, string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = startLocation,
            ShowOverwritePrompt = true,
        });
        return file is null ? null : file.TryGetLocalPath();
    }

    // Every open text editor window: a file already open is brought to the front, not opened twice.
    private readonly List<TextEditorWindow> _textEditors = [];

    internal IReadOnlyList<TextEditorWindow> TextEditors => _textEditors;

    public async Task ShowTextEditorAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_textEditors.FirstOrDefault(w => string.Equals(w.ViewModel.FilePath, fullPath, comparison)) is { } open)
        {
            open.Activate();
            return;
        }

        TextEditorWindowViewModel viewModel;
        try
        {
            viewModel = await TextEditorWindowViewModel.LoadAsync(fullPath, this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            await ShowErrorAsync("Open Text File Failed", $"Could not open '{fullPath}':\n{ex.Message}");
            return;
        }

        var window = new TextEditorWindow(viewModel, _themeService, _preferences);
        _textEditors.Add(window);
        window.Closed += (_, _) => _textEditors.Remove(window);

        var owner = MainWindow;
        if (owner is not null) window.Show(owner);
        else window.Show();
    }

    public async Task<string?> ShowSaveCodeFileDialogAsync(string suggestedFileName, string extension, string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Code",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            SuggestedStartLocation = startLocation,
            FileTypeChoices = [extension == "il"
                ? new FilePickerFileType("IL") { Patterns = ["*.il"] }
                : new FilePickerFileType("C#") { Patterns = ["*.cs"] }],
        });
        return file is null ? null : file.TryGetLocalPath();
    }

    // One assembly explorer for the app (it has tabs now): the menu and every F12 into decompiled code
    // reuse it while it's open; closing it lets the next one create a fresh window.
    private AssemblyExplorerWindow? _assemblyExplorer;

    internal AssemblyExplorerWindow? AssemblyExplorer => _assemblyExplorer;

    public Task ShowAssemblyExplorerAsync(string startFolder)
    {
        if (_assemblyExplorer is { } open)
        {
            open.Activate();
            return Task.CompletedTask;
        }

        ShowTracked(new AssemblyExplorerWindow(_themeService, this, startFolder) { Preferences = _preferences });
        return Task.CompletedTask;
    }

    public async Task ShowDefinitionInAssemblyExplorerAsync(DecompiledMemberResult decompiled, IDefinitionResult definition)
    {
        if (_assemblyExplorer is { } open)
        {
            open.Activate();
            await open.OpenDefinitionInNewTabAsync(decompiled, definition.AssemblyPath, definition);
            return;
        }

        ShowTracked(new AssemblyExplorerWindow(_themeService, this, decompiled, definition.AssemblyPath, definition) { Preferences = _preferences });
    }

    private void ShowTracked(AssemblyExplorerWindow window)
    {
        _assemblyExplorer = window;
        window.Closed += (_, _) =>
        {
            if (_assemblyExplorer == window) _assemblyExplorer = null;
        };

        var owner = MainWindow;
        if (owner is not null) window.Show(owner);
        else window.Show();
    }

    private static Task ShowDialogAsync(Window dialog)
    {
        var owner = MainWindow;
        return owner is not null ? dialog.ShowDialog(owner) : Task.CompletedTask;
    }
}
