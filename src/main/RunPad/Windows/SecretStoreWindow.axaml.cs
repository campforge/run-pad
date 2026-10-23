using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.Services;
using RunPad.Windows.Models;

namespace RunPad.Windows;

// Closes with true after the store was saved, false/null otherwise.
public partial class SecretStoreWindow : Window
{
    public SecretStoreWindow(SecretStoreWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    public SecretStoreWindow() => InitializeComponent();

    private SecretStoreWindowViewModel? ViewModel => DataContext as SecretStoreWindowViewModel;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Close(true);
    }

    private bool _askingToSave;

    private async void Cancel_Click(object? sender, RoutedEventArgs e) => await CloseAskingToSaveAsync();

    // The window's own close button asks too. A programmatic Close (Save/Cancel, the app shutting
    // down, tests) never does.
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
                .WithMessage("The Secret Store has unsaved changes. Save them?")
                .ShowDialog<UnsavedChangesResult?>(this);
            switch (choice)
            {
                // A failed save (an invalid entry, a store that changed) keeps the window open.
                case UnsavedChangesResult.Save when vm.Save():
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
}
