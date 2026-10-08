using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RunPad.Windows.Models;

namespace RunPad.Windows;

// Closes with true after the sources were saved, false/null otherwise.
public partial class NuGetSourcesWindow : Window
{
    public NuGetSourcesWindow(NuGetSourcesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public NuGetSourcesWindow() => InitializeComponent();

    private NuGetSourcesWindowViewModel? ViewModel => DataContext as NuGetSourcesWindowViewModel;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedSource is not { } source) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Package Source Folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                source.Url = path;
        }
        catch (Exception)
        {
            // No folder picker on this platform/session — the path can still be typed.
        }
    }
}
