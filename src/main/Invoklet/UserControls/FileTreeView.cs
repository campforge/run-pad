using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Format.MdRun;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

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

// Same ParentPath convention as FileTreeCreateFolderEventArgs; Language comes from
// which "New File" submenu entry (C#/Visual Basic) was clicked, same split as MainWindow's
// File → New Invoklet File menu.
public sealed class FileTreeCreateFileEventArgs(RoutedEvent routedEvent, string? parentPath, MdRunLanguage language)
    : RoutedEventArgs(routedEvent)
{
    public string? ParentPath { get; } = parentPath;
    public MdRunLanguage Language { get; } = language;
}

// SourcePath is the dragged node; TargetPath follows the exact same convention as
// FileTreeCreateFolderEventArgs.ParentPath (null means "dropped on empty tree space, use the
// workspace root" — see MainWindowViewModel.ResolveParentDir, reused as-is by MoveFileOrFolder).
public sealed class FileTreeMoveEventArgs(RoutedEvent routedEvent, string sourcePath, string? targetPath)
    : RoutedEventArgs(routedEvent)
{
    public string SourcePath { get; } = sourcePath;
    public string? TargetPath { get; } = targetPath;
}

public partial class FileTreeView : UserControl
{
    private readonly TreeView _fileTree;

    // A custom, app-private drag format (not DataFormat.Text) so an in-tree node drag
    // isn't mistaken for/offered as plain text to whatever else might be watching drag-and-drop.
    // internal (not private) + InternalsVisibleTo so tests can build matching drag data directly,
    // same "internal test hook" pattern used elsewhere in this project.
    internal static readonly DataFormat<string> NodePathFormat =
        DataFormat.CreateStringApplicationFormat("Invoklet-filetree-node-path");

    public FileTreeView()
    {
        InitializeComponent();
        _fileTree = this.FindControl<TreeView>("FileTree")!;

        // TreeViewItem's own built-in click handling marks PointerPressed Handled
        // once it's done with it (selection/expand-collapse) — a plain XAML `PointerPressed="..."`
        // attribute (the default, handledEventsToo: false) on the outer TreeView never saw a single
        // left-click on a row because of this, which is why drag never started at all. Same fix
        // shape as MainWindow.OnWindowKeyDown's own handledEventsToo: true (there, for AvaloniaEdit
        // marking KeyDown handled).
        _fileTree.AddHandler(PointerPressedEvent, FileTree_PointerPressed, handledEventsToo: true);
        _fileTree.AddHandler(PointerMovedEvent, FileTree_PointerMoved, handledEventsToo: true);
        DragDrop.SetAllowDrop(_fileTree, true);
        _fileTree.AddHandler(DragDrop.DragEnterEvent, FileTree_DragEnter);
        _fileTree.AddHandler(DragDrop.DragOverEvent, FileTree_DragOver);
        _fileTree.AddHandler(DragDrop.DragLeaveEvent, FileTree_DragLeave);
        _fileTree.AddHandler(DragDrop.DropEvent, FileTree_Drop);
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

    public static readonly RoutedEvent<FileTreeCreateFileEventArgs> CreateFileEvent =
        RoutedEvent.Register<FileTreeView, FileTreeCreateFileEventArgs>(nameof(CreateFile), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<FileTreeMoveEventArgs> MoveEvent =
        RoutedEvent.Register<FileTreeView, FileTreeMoveEventArgs>(nameof(Move), RoutingStrategies.Bubble);

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

    /// <summary>Create-new-file command.</summary>
    public event EventHandler<FileTreeCreateFileEventArgs>? CreateFile
    {
        add => AddHandler(CreateFileEvent, value);
        remove => RemoveHandler(CreateFileEvent, value);
    }

    /// <summary>A node was dropped onto another node (or empty tree space) — move it there.</summary>
    public event EventHandler<FileTreeMoveEventArgs>? Move
    {
        add => AddHandler(MoveEvent, value);
        remove => RemoveHandler(MoveEvent, value);
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
        var point = e.GetCurrentPoint(_fileTree);

        if (point.Properties.IsRightButtonPressed)
        {
            if (NodeAt(e.Source) is { } node)
                _fileTree.SelectedItem = node;
            return;
        }

        // Treat the folder row as a disclosure target, not just the tiny chevron. This keeps
        // the tree usable with a trackpad and matches the one-click behaviour of the other
        // navigation trees. Do not toggle again when the actual chevron was pressed.
        if (point.Properties.IsLeftButtonPressed && NodeAt(e.Source) is { IsFolder: true } folder)
        {
            var visual = e.Source as Visual;
            var clickedChevron = false;
            while (visual is not null && visual is not TreeViewItem)
            {
                clickedChevron |= visual is Control { Name: "PART_ExpandCollapseChevron" };
                visual = visual.GetVisualParent();
            }

            if (!clickedChevron && visual is TreeViewItem item)
                item.IsExpanded = !item.IsExpanded;
        }

        // A left-press on a node is only a POSSIBLE drag start, recorded here but not
        // acted on yet: calling DoDragDropAsync immediately (right on press, before any real
        // movement) hijacked every plain click — a single click on a folder stopped expanding it, and
        // stopped selecting/opening a file, because starting the drag session took over the pointer
        // before the TreeView's own normal click handling got a chance to run. e.Handled is left
        // alone here for the same reason. FileTree_PointerMoved below decides whether this actually
        // turns into a drag, once/if the pointer moves far enough while still held.
        _possibleDragStart = point.Properties.IsLeftButtonPressed && NodeAt(e.Source) is { } pressedNode
            ? (e.GetPosition(_fileTree), e, pressedNode)
            : null;
    }

    private const int MinimumDragDistance = 4;
    private (Point Position, PointerPressedEventArgs Args, FileTreeViewNode Node)? _possibleDragStart;

    private void FileTree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_possibleDragStart is not { } start || start.Args.Pointer.Id != e.Pointer.Id) return;

        if (!e.GetCurrentPoint(_fileTree).Properties.IsLeftButtonPressed)
        {
            _possibleDragStart = null;
            return;
        }

        var movement = e.GetPosition(_fileTree) - start.Position;
        if (Math.Abs(movement.X) <= MinimumDragDistance && Math.Abs(movement.Y) <= MinimumDragDistance) return;

        _possibleDragStart = null;
        _ = StartDragAsync(start.Args, start.Node);
    }

    private static FileTreeViewNode? NodeAt(object? eventSource)
    {
        Visual? v = eventSource as Visual;
        while (v is not null and not TreeViewItem)
            v = v.GetVisualParent();

        return (v as TreeViewItem)?.DataContext as FileTreeViewNode;
    }

    // ── File tree — drag-and-drop (move a node into another folder) ────────────

    private static async Task StartDragAsync(PointerPressedEventArgs args, FileTreeViewNode node)
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(NodePathFormat, node.FilePath);
        data.Add(item);

        await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
    }

    // Dropping onto a folder node moves INSIDE it; onto a file node moves alongside it (same
    // parent); onto empty tree space (no node under the pointer) moves to the tree's root —
    // deliberately the exact same target-directory semantics as "New Folder"/"New File" already use
    // (see ResolveParentDir), so a drop target is never rejected. e.DataTransfer.Contains(...) still
    // gates it — only a drag that actually originated from this tree (carries NodePathFormat) is
    // accepted; an OS-level drag of external files onto the tree is not supported.
    //
    // DragEnter needs this SAME check, not just DragOver — the cursor otherwise latched onto
    // whatever the platform's default (no-drop) is for the entire drag, never updating even once
    // DragOver started returning Move, because nothing ever told it a drop was allowed in the first
    // place (same two-handler shape AvaloniaEdit's own SelectionMouseHandler uses for text drag-drop).
    private void FileTree_DragEnter(object? sender, DragEventArgs e) => FileTree_DragOver(sender, e);

    private void FileTree_DragOver(object? sender, DragEventArgs e)
    {
        var isValidDrag = e.DataTransfer.Contains(NodePathFormat);
        e.DragEffects = isValidDrag ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        UpdateDropHighlight(isValidDrag ? NodeAt(e.Source) : null, isValidDrag);
    }

    private void FileTree_DragLeave(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropHighlight();
    }

    private void FileTree_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var targetPath = NodeAt(e.Source)?.FilePath;
        ClearDropHighlight();
        if (e.DataTransfer.TryGetValue(NodePathFormat) is not { } sourcePath) return;

        RaiseEvent(new FileTreeMoveEventArgs(MoveEvent, sourcePath, targetPath));
    }

    // Live user request: the OS drag cursor alone isn't a reliable-enough visual
    // signal of where a drop will land, so the current drop target is highlighted directly too —
    // either a specific node (IsDropTarget, bound in the item template) or, for empty tree space
    // (isValidDrag but no node under the pointer — dropping there targets the workspace root), the
    // whole tree's own Background.
    private FileTreeViewNode? _dropTargetNode;

    private void UpdateDropHighlight(FileTreeViewNode? node, bool isValidDrag)
    {
        if (node != _dropTargetNode)
        {
            _dropTargetNode?.IsDropTarget = false;
            node?.IsDropTarget = true;
            _dropTargetNode = node;
        }

        _fileTree.Background = isValidDrag && node is null && this.TryFindResource(
            "Invoklet.Accent.BackgroundBrush", out var brush) && brush is IBrush accentBrush
            ? accentBrush
            : Brushes.Transparent;
    }

    private void ClearDropHighlight() => UpdateDropHighlight(node: null, isValidDrag: false);

    // ── File tree — F2 / Delete hotkeys ──────────────────────────────────────

    // Used to check literal Key.F2/Key.Delete — this control has no DI (see
    // FileTreeViewModel's own doc comment on why), so the gestures are read from its DataContext
    // instead, live on every keypress rather than a value cached at construction.
    private void FileTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_fileTree.SelectedItem is not FileTreeViewNode node) return;
        if (DataContext is not FileTreeViewModel vm) return;

        if (Match(e, vm.RenameGesture))
        {
            StartRename(node);
            e.Handled = true;
        }
        else if (Match(e, vm.DeleteGesture))
        {
            RaiseEvent(new FileTreeFileEventArgs(DeleteEvent, node.FilePath));
            e.Handled = true;
        }
    }

    private static bool Match(KeyEventArgs e, string gesture)
        => KeyGestureText.Parse(gesture)?.Matches(e) == true;

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

    private void ContextCreateFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MdRunLanguage language })
            RaiseEvent(new FileTreeCreateFileEventArgs(
                CreateFileEvent, (_fileTree.SelectedItem as FileTreeViewNode)?.FilePath, language));
    }

    // The New File submenu offers the registered languages, with their own names and icons.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var menu = this.FindControl<MenuItem>("NewFileMenu")!;
        menu.Items.Clear();
        if (DataContext is not FileTreeViewModel vm) return;
        foreach (var language in vm.NewFileLanguages)
        {
            var item = new MenuItem { Header = language.DisplayName, Tag = language.Language };
            // The data context can arrive before the view is in a window: then only the app has the icons.
            if (language.IconResourceKey is { } key
                && (this.TryFindResource(key, out var icon) || Application.Current?.TryFindResource(key, out icon) == true)
                && icon is Geometry data)
                item.Icon = new PathIcon { Data = data, Width = 16, Height = 16 };
            item.Click += ContextCreateFile_Click;
            menu.Items.Add(item);
        }
    }

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
