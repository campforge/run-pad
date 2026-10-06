using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI;
using RunPad.Format.RPad;
using RunPad.Windows.Models;

namespace RunPad.Windows;

public partial class DependenciesWindow : Window
{
    private static readonly FilePickerFileType ReferenceFileType = new("Assemblies and projects")
    {
        Patterns = ["*.dll", "*.csproj", "*.vbproj", "*.fsproj"]
    };

    public DependenciesWindow(DependenciesWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => SearchBox.Focus();
        Closed += (_, _) => viewModel.Dispose();
    }

    public DependenciesWindow() => InitializeComponent();

    private DependenciesWindowViewModel? ViewModel => DataContext as DependenciesWindowViewModel;

    public void SetFrontMatter(RpadFrontMatter? fm) => ViewModel?.SetFrontMatter(fm);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ApplyChanges();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    // Enter searches right away instead of waiting for the debounce; it never applies the dialog.
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        e.Handled = true;
        ViewModel?.SearchNow();
    }

    private void ReferenceBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || ViewModel is not { } vm) return;
        e.Handled = true;
        Execute(vm.AddReferenceCommand);
    }

    private void NamespaceBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || ViewModel is not { } vm) return;
        e.Handled = true;
        Execute(vm.AddNamespaceCommand);
    }

    // Delete (Backspace on a Mac keyboard) removes the selected reference/import.
    private void EntryList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back)) return;
        if (sender is not ListBox { SelectedItem: DependencyEntryViewModel entry }) return;
        e.Handled = true;
        Execute(entry.RemoveCommand);
    }

    private async void BrowseReference_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add Reference",
                AllowMultiple = true,
                FileTypeFilter = [ReferenceFileType, FilePickerFileTypes.All],
            });
            foreach (var file in files)
                if (file.TryGetLocalPath() is { } path)
                    vm.AddReference(path);
        }
        catch (Exception ex)
        {
            vm.ReferenceError = $"Couldn't open the file picker: {ex.Message}";
        }
    }

    private static void Execute(ReactiveCommand<Unit, Unit> command)
        => _ = command.Execute(Unit.Default).Subscribe(_ => { }, _ => { });
}
