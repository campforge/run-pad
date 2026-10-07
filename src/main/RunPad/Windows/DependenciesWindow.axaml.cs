using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI;
using RunPad.Format.RPad;
using RunPad.Services;
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
        Closing += OnClosing;
        Closed += (_, _) => viewModel.Dispose();
    }

    public DependenciesWindow() => InitializeComponent();

    private DependenciesWindowViewModel? ViewModel => DataContext as DependenciesWindowViewModel;

    private bool _askingToSave;

    public void SetFrontMatter(RpadFrontMatter? fm) => ViewModel?.SetFrontMatter(fm);

    // Save is only enabled while there is something to apply (CanApply).
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanApply: true } vm) return;
        vm.ApplyChanges();
        Close(true);
    }

    // Cancel and Escape (IsCancel) ask before dropping pending changes.
    private async void Cancel_Click(object? sender, RoutedEventArgs e) => await CloseAskingToSaveAsync();

    // The window's own close button asks too. A programmatic Close (Save/Cancel above, the app
    // shutting down, tests) never does.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.IsProgrammatic || ViewModel is not { HasChanges: true }) return;
        e.Cancel = true;
        await CloseAskingToSaveAsync();
    }

    internal async Task CloseAskingToSaveAsync()
    {
        if (_askingToSave) return;
        if (ViewModel is not { HasChanges: true } vm)
        {
            Close(false);
            return;
        }

        _askingToSave = true;
        try
        {
            var choice = await UnsavedChangesDialog
                .WithMessage("The script's dependencies have unsaved changes. Save them?")
                .ShowDialog<UnsavedChangesResult?>(this);
            switch (choice)
            {
                // A blocking error (an invalid namespace line) keeps the window open to fix it.
                case UnsavedChangesResult.Save when vm.ApplyChanges():
                    Close(true);
                    break;
                case UnsavedChangesResult.DontSave:
                    Close(false);
                    break;
            }
        }
        finally
        {
            _askingToSave = false;
        }
    }

    // Enter searches right away instead of waiting for the debounce; it never applies the dialog.
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        e.Handled = true;
        ViewModel?.SearchNow();
    }

    // "(configure sources...)" in the source ComboBox is a command, not a source: put the previous
    // source back (the VM never accepts the item) and open the configurator, LINQPad-style.
    private async void SourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not ComboBox box) return;
        if (box.SelectedItem is not PackageSourceOption { IsConfigure: true }) return;

        box.SelectedItem = vm.SelectedSourceOption;
        await ShowSourcesConfiguratorAsync();
    }

    internal async Task ShowSourcesConfiguratorAsync()
    {
        if (ViewModel is not { } vm) return;
        var saved = await new NuGetSourcesWindow(vm.CreateSourcesEditor()).ShowDialog<bool?>(this);
        if (saved == true)
            vm.ReloadSources();
    }

    private void ReferenceBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || ViewModel is not { } vm) return;
        e.Handled = true;
        Execute(vm.AddReferenceCommand);
    }

    // Delete (Backspace on a Mac keyboard) removes the selected reference.
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
