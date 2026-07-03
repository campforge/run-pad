using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotPad.Assets.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Microsoft.Extensions.Logging;

namespace DotPad.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Func<DependenciesWindow> _dependenciesWindowFactory;
    private readonly Func<PreferencesWindow> _preferencesWindowFactory;
    private readonly IPreferencesService _prefs;

    public MainWindow(
        IThemeService themeService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        ILoggerFactory loggerFactory,
        IIntelliSenseService intelliSense,
        Func<DependenciesWindow> dependenciesWindowFactory,
        Func<PreferencesWindow> preferencesWindowFactory,
        MainWindowViewModel viewModel)
    {
        _dependenciesWindowFactory = dependenciesWindowFactory;
        _preferencesWindowFactory = preferencesWindowFactory;
        _prefs = prefs;
        DataContext = viewModel;

        InitializeComponent();

        // handledEventsToo: true — fires even when AvaloniaEdit marks the event Handled
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        var fileTree = this.FindControl<TreeView>("FileTree");
        if (fileTree is not null)
            fileTree.SelectionChanged += FileTree_SelectionChanged;

        EditorTabsCtrl.Initialize(viewModel, themeService, registry, prefs, loggerFactory, intelliSense);
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var kb = _prefs.Keyboard;
        if (Match(e, kb.Run))
        {
            EditorTabsCtrl.RunOrContinueActive();
            e.Handled = true;
        }
        else if (Match(e, kb.Cancel) && (DataContext as MainWindowViewModel)?.ActiveTab?.EnableCancelButton == true)
        {
            EditorTabsCtrl.CancelActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOver))
        {
            EditorTabsCtrl.StepOverActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepIn))
        {
            EditorTabsCtrl.StepInActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOut))
        {
            EditorTabsCtrl.StepOutActive();
            e.Handled = true;
        }
    }

    private static bool Match(KeyEventArgs e, string gesture)
    {
        try { return KeyGesture.Parse(gesture).Matches(e); }
        catch { return false; }
    }

    // ── Menu handlers ─────────────────────────────────────────────────────────

    private void NewRpadFile_Click(object? sender, RoutedEventArgs e)
        => GetViewModel().NewRpadFile();

    private void Dependencies_Click(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
            _dependenciesWindowFactory().ShowDialog(this));

    private void Preferences_Click(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
            _preferencesWindowFactory().ShowDialog(this));

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
