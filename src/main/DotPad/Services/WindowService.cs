using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DotPad.Format.RPad;
using DotPad.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace DotPad.Services;

public sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _sp;

    public WindowService(IServiceProvider sp) => _sp = sp;

    public async Task ShowDependenciesAsync(IObservable<RpadFrontMatter?>? frontMatter = null)
    {
        var window = _sp.GetRequiredService<DependenciesWindow>();
        using var subscription = frontMatter?.Subscribe(window.SetFrontMatter);
        await ShowDialogAsync(window);
    }

    public Task ShowPreferencesAsync()
        => ShowDialogAsync(_sp.GetRequiredService<PreferencesWindow>());

    public Task ShowErrorAsync(string title, string message)
        => ShowDialogAsync(new ErrorDialog(title, message));

    public Task ShowAboutAsync()
        => ShowDialogAsync(new AboutWindow());

    public async Task<string?> ShowSaveFileDialogAsync(string suggestedFileName, string startFolder)
    {
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
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

    private static Task ShowDialogAsync(Window dialog)
    {
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return owner is not null ? dialog.ShowDialog(owner) : Task.CompletedTask;
    }
}
