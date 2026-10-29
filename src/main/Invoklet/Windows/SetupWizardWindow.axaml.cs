using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Invoklet.Windows.Models;

namespace Invoklet.Windows;

public partial class SetupWizardWindow : Window
{
    public event Action? Continued;

    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public SetupWizardWindow() => InitializeComponent();

    private void Continue_Click(object? sender, RoutedEventArgs e) => Continued?.Invoke();

    // No FileTypeFilter — the real executable is "dotnet.exe"/"netcoredbg.exe" on Windows but plain
    // "dotnet"/"netcoredbg" (no extension) on Linux/macOS, so any extension-based pattern would only
    // ever be right on one platform.
    private async void BrowseDotnet_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupWizardViewModel viewModel) return;
        var suggestedStartLocation = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : "/usr/";
        var t = await StorageProvider.TryGetFolderFromPathAsync(suggestedStartLocation);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Browse for the dotnet executable",
            AllowMultiple = false,
            SuggestedStartLocation = t
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) viewModel.SetDotnetPathManually(path);
    }

    private async void BrowseDebugger_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupWizardViewModel viewModel) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Browse for the netcoredbg executable",
            AllowMultiple = false,
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) viewModel.SetDebuggerPathManually(path);
    }
}
