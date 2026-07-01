using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotPad.Assets.Models;
using DotPad.Contracts;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotPad.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(
        IThemeService         themeService,
        ILanguageHostRegistry registry,
        IPreferencesService   prefs,
        ILoggerFactory        loggerFactory,
        IServiceProvider      serviceProvider,
        MainWindowViewModel   viewModel)
    {
        _serviceProvider = serviceProvider;
        DataContext      = viewModel;

        InitializeComponent();

        var fileTree = this.FindControl<TreeView>("FileTree");
        if (fileTree is not null)
            fileTree.SelectionChanged += FileTree_SelectionChanged;

        EditorTabsCtrl.Initialize(viewModel, themeService, registry, prefs, loggerFactory);
    }

    private void Dependencies_Click(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
            _serviceProvider.GetRequiredService<DependenciesWindow>().ShowDialog(this));

    private void Preferences_Click(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
            _serviceProvider.GetRequiredService<PreferencesWindow>().ShowDialog(this));

    private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not Node { SubNodes: null } node) return;
        GetViewModel().OpenTab(node.FilePath);
    }

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException(
                $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");

    public void Dispose()
    {
        EditorTabsCtrl.Dispose();
        GC.SuppressFinalize(this);
    }
}
