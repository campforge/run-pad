using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using RunPad.Format.RPad;
using RunPad.Windows;

namespace RunPad.Services;

// TODO.md #100: used to take the whole IServiceProvider and resolve dependencies inside each method
// (Service Locator — DIP violation, constructor didn't document real dependencies, hard to test in
// isolation). Explicit Func<TWindow> factories instead — same technique already used for
// Func<IIntelliSenseService> in App.axaml.cs's ConfigureServices, registered there the same way.
// DependenciesWindow/PreferencesWindow are DI-registered as Transient (a fresh instance each time
// they're shown), so a Func<T> factory — not a direct T — is the right shape for both.
public sealed class WindowService : IWindowService
{
    private readonly Func<DependenciesWindow> _dependenciesWindowFactory;
    private readonly Func<PreferencesWindow> _preferencesWindowFactory;
    private readonly IThemeService _themeService;

    public WindowService(
        Func<DependenciesWindow> dependenciesWindowFactory,
        Func<PreferencesWindow> preferencesWindowFactory,
        IThemeService themeService)
    {
        _dependenciesWindowFactory = dependenciesWindowFactory;
        _preferencesWindowFactory = preferencesWindowFactory;
        _themeService = themeService;
    }

    // TODO.md #101: the same lookup, repeated verbatim 6 times below.
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

    public async Task<string?> ShowOpenLinqFileDialogAsync(string startFolder)
    {
        var owner = MainWindow;
        if (owner is null) return null;

        var startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import LINQPad Query",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = [new FilePickerFileType("LINQPad Query") { Patterns = ["*.linq"] }],
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

    public Task ShowAssemblyExplorerAsync(string startFolder)
    {
        var owner = MainWindow;

        var window = new AssemblyExplorerWindow(_themeService, this, startFolder);
        if (owner is not null) window.Show(owner);
        else window.Show();
        return Task.CompletedTask;
    }

    private static Task ShowDialogAsync(Window dialog)
    {
        var owner = MainWindow;
        return owner is not null ? dialog.ShowDialog(owner) : Task.CompletedTask;
    }
}
