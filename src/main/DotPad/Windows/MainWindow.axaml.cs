using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // ── Global keyboard shortcuts ─────────────────────────────────────────────

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

    // ── Title bar window controls ─────────────────────────────────────────────

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized 
            ? WindowState.Normal 
            : WindowState.Maximized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
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

    // ── File tree selection ───────────────────────────────────────────────────

    private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not Node { SubNodes: null } node) return;
        GetViewModel().OpenTab(node.FilePath);
    }

    // ── File tree — right-click selects item before context menu opens ─────────

    private void FileTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileTree).Properties.IsRightButtonPressed) return;

        // Walk up the visual tree to find the TreeViewItem that was pressed
        Visual? v = e.Source as Visual;
        while (v is not null and not TreeViewItem)
            v = v.GetVisualParent();

        if (v is TreeViewItem { DataContext: Node node })
            FileTree.SelectedItem = node;
    }

    // ── File tree — F2 / Delete hotkeys ──────────────────────────────────────

    private void FileTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (FileTree.SelectedItem is not Node node) return;

        if (e.Key == Key.F2)
        {
            StartRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            GetViewModel().DeleteNode(node);
            e.Handled = true;
        }
    }

    // ── Rename helpers ────────────────────────────────────────────────────────

    private void StartRename(Node node)
    {
        GetViewModel().BeginRenameNode(node);

        // Focus the TextBox after the binding update propagates
        Dispatcher.UIThread.Post(() =>
        {
            if (FileTree.ContainerFromItem(node) is not TreeViewItem tvi) return;
            var box = tvi.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible);
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not Node node) return;

        if (e.Key == Key.Return)
        {
            GetViewModel().CommitRenameNode(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            GetViewModel().CancelRenameNode(node);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // Guard against double-commit when Enter already set IsRenaming=false
        if (sender is TextBox box && box.DataContext is Node { IsRenaming: true } node)
            GetViewModel().CommitRenameNode(node);
    }

    // ── Context menu handlers ─────────────────────────────────────────────────

    private void ContextRename_Click(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is Node node)
            StartRename(node);
    }

    private void ContextDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is Node node)
            GetViewModel().DeleteNode(node);
    }

    private void ContextOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is Node node)
            GetViewModel().OpenContainingFolder(node);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
