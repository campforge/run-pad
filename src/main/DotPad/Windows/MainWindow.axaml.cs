using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotPad.Assets.Models;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;

namespace DotPad.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly IPreferencesService _prefs;
    private readonly EditorTabsControl _editorTabs;

    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel)
    {
        _prefs = prefs;
        _editorTabs = editorTabsCtrl;

        DataContext = viewModel;

        InitializeComponent();

        // handledEventsToo: true — fires even when AvaloniaEdit marks the event Handled
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        var fileTree = this.FindControl<TreeView>("FileTree");
        if (fileTree is not null)
            fileTree.SelectionChanged += FileTree_SelectionChanged;

        this.FindControl<ContentControl>("EditorTabsHost")!.Content = editorTabsCtrl;
        editorTabsCtrl.Attach(viewModel);
    }

    // ── Global keyboard shortcuts ─────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var kb = _prefs.Keyboard;
        var vm = GetViewModel();

        if (Match(e, kb.Run))
        {
            _editorTabs.RunOrContinueActive();
            e.Handled = true;
        }
        else if (Match(e, kb.Cancel) && vm.ActiveTab?.EnableCancelButton == true)
        {
            _editorTabs.CancelActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOver))
        {
            _editorTabs.StepOverActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepIn))
        {
            _editorTabs.StepInActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOut))
        {
            _editorTabs.StepOutActive();
            e.Handled = true;
        }
        else if (e.Key == Key.F4)
        {
            vm.OpenDependenciesWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.CloseActiveTab();
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
        => GetViewModel().NewRpadFile();  // creates a new .rpad file (Statements kind by default)

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
        _editorTabs.Dispose();
        GC.SuppressFinalize(this);
    }
}
