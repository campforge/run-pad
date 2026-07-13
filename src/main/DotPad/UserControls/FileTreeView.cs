using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotPad.UserControls.Models;

namespace DotPad.UserControls;

public sealed class FileTreeFileEventArgs(RoutedEvent routedEvent, string filePath) : RoutedEventArgs(routedEvent)
{
    public string FilePath { get; } = filePath;
}

public sealed class FileTreeRenameEventArgs(RoutedEvent routedEvent, string filePath, string newName)
    : RoutedEventArgs(routedEvent)
{
    public string FilePath { get; } = filePath;
    public string NewName { get; } = newName;
}

// ParentPath is null when nothing's selected (create at the tree's root) — see
// MainWindowViewModel.CreateFolder for how it's resolved into an actual directory.
public sealed class FileTreeCreateFolderEventArgs(RoutedEvent routedEvent, string? parentPath)
    : RoutedEventArgs(routedEvent)
{
    public string? ParentPath { get; } = parentPath;
}

public partial class FileTreeView : UserControl
{
    private readonly TreeView _fileTree;

    public FileTreeView()
    {
        InitializeComponent();
        _fileTree = this.FindControl<TreeView>("FileTree")!;
    }

    #region UserControlEvents

    // Compiled XAML resolves an `Open="..."` event attribute by reaching for a field named
    // `OpenEvent` directly (not through the `Open` property's add/remove) — these must stay public.
    public static readonly RoutedEvent<FileTreeFileEventArgs> OpenEvent =
        RoutedEvent.Register<FileTreeView, FileTreeFileEventArgs>(nameof(Open), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<FileTreeFileEventArgs> DeleteEvent =
        RoutedEvent.Register<FileTreeView, FileTreeFileEventArgs>(nameof(Delete), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<FileTreeFileEventArgs> OpenFolderEvent =
        RoutedEvent.Register<FileTreeView, FileTreeFileEventArgs>(nameof(OpenFolder), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<FileTreeRenameEventArgs> RenameEvent =
        RoutedEvent.Register<FileTreeView, FileTreeRenameEventArgs>(nameof(Rename), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<FileTreeCreateFolderEventArgs> CreateFolderEvent =
        RoutedEvent.Register<FileTreeView, FileTreeCreateFolderEventArgs>(nameof(CreateFolder), RoutingStrategies.Bubble);

    /// <summary>A file node was selected (opened) in the tree.</summary>
    public event EventHandler<FileTreeFileEventArgs>? Open
    {
        add => AddHandler(OpenEvent, value);
        remove => RemoveHandler(OpenEvent, value);
    }

    /// <summary>Delete file command.</summary>
    public event EventHandler<FileTreeFileEventArgs>? Delete
    {
        add => AddHandler(DeleteEvent, value);
        remove => RemoveHandler(DeleteEvent, value);
    }

    /// <summary>Open the folder containing this file.</summary>
    public event EventHandler<FileTreeFileEventArgs>? OpenFolder
    {
        add => AddHandler(OpenFolderEvent, value);
        remove => RemoveHandler(OpenFolderEvent, value);
    }

    /// <summary>The user committed a rename (new name, extension excluded).</summary>
    public event EventHandler<FileTreeRenameEventArgs>? Rename
    {
        add => AddHandler(RenameEvent, value);
        remove => RemoveHandler(RenameEvent, value);
    }

    /// <summary>Create-new-folder command.</summary>
    public event EventHandler<FileTreeCreateFolderEventArgs>? CreateFolder
    {
        add => AddHandler(CreateFolderEvent, value);
        remove => RemoveHandler(CreateFolderEvent, value);
    }

    #endregion

    // ── File tree selection ───────────────────────────────────────────────────

    private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not FileTreeViewNode { SubNodes: null } node) return;
        RaiseEvent(new FileTreeFileEventArgs(OpenEvent, node.FilePath));
    }

    // ── File tree — right-click selects item before context menu opens ─────────

    private void FileTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_fileTree).Properties.IsRightButtonPressed) return;

        Visual? v = e.Source as Visual;
        while (v is not null and not TreeViewItem)
            v = v.GetVisualParent();

        if (v is TreeViewItem { DataContext: FileTreeViewNode node })
            _fileTree.SelectedItem = node;
    }

    // ── File tree — F2 / Delete hotkeys ──────────────────────────────────────

    private void FileTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_fileTree.SelectedItem is not FileTreeViewNode node) return;

        if (e.Key == Key.F2)
        {
            StartRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RaiseEvent(new FileTreeFileEventArgs(DeleteEvent, node.FilePath));
            e.Handled = true;
        }
    }

    // ── Rename helpers ────────────────────────────────────────────────────────

    private void StartRename(FileTreeViewNode node)
    {
        node.EditingName = node.DisplayName;
        node.IsRenaming = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (_fileTree.ContainerFromItem(node) is not TreeViewItem tvi) return;
            var box = tvi.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible);
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void CommitRename(FileTreeViewNode node)
    {
        node.IsRenaming = false;

        var newName = node.EditingName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == node.DisplayName)
            return;

        RaiseEvent(new FileTreeRenameEventArgs(RenameEvent, node.FilePath, newName));
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileTreeViewNode node) return;

        if (e.Key == Key.Return)
        {
            CommitRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is FileTreeViewNode { IsRenaming: true } node)
            CommitRename(node);
    }

    // ── Context menu handlers ─────────────────────────────────────────────────

    private void ContextRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_fileTree.SelectedItem is FileTreeViewNode node)
            StartRename(node);
    }

    private void ContextDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_fileTree.SelectedItem is FileTreeViewNode node)
            RaiseEvent(new FileTreeFileEventArgs(DeleteEvent, node.FilePath));
    }

    private void ContextOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_fileTree.SelectedItem is FileTreeViewNode node)
            RaiseEvent(new FileTreeFileEventArgs(OpenFolderEvent, node.FilePath));
    }

    private void ContextCreateFolder_Click(object? sender, RoutedEventArgs e)
        => RaiseEvent(new FileTreeCreateFolderEventArgs(
            CreateFolderEvent, (_fileTree.SelectedItem as FileTreeViewNode)?.FilePath));

    /// <summary>Enters rename mode for the node at <paramref name="filePath"/>, if it's currently in
    /// the tree — called back from MainWindow right after CreateFolder completes, so a freshly
    /// created folder is immediately ready to be named (VS/Rider/Explorer-style), same UX as the
    /// existing F2/"Rename" flow.</summary>
    public void StartRenameForPath(string filePath)
    {
        if (DataContext is FileTreeViewModel vm && vm.FindByPath(filePath) is { } node)
            StartRename(node);
    }
}
