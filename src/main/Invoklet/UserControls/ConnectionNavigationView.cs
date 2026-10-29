using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

/// <summary>Asks the window to open the Connections editor, at a connection when there is one.</summary>
public sealed class ConnectionNavigationEventArgs(RoutedEvent routedEvent, string? connectionName) : RoutedEventArgs(routedEvent)
{
    public string? ConnectionName { get; } = connectionName;
}

/// <summary>
/// The sidebar's Connections tree. Keyboard: F2 renames a group, Delete removes one (its contents
/// move up), Enter opens a connection, Alt+Up/Down reorders a group, and the context menu
/// (Shift+F10 or the menu key) holds every action, including Add to Script and Move To. Rows can
/// also be dragged onto a group, or onto a top-level connection or empty space for the top level; a
/// connection dragged onto the editor is added to the active script (MainWindow).
/// </summary>
public partial class ConnectionNavigationView : UserControl
{
    internal static readonly DataFormat<string> NodeFormat =
        DataFormat.CreateStringApplicationFormat("Invoklet-connection-node");

    private const int MinimumDragDistance = 4;
    private readonly TreeView _tree;
    private (Point Position, PointerPressedEventArgs Args, ConnectionNavigationNode Node)? _possibleDragStart;
    private ConnectionNavigationNode? _dropTarget;

    public static readonly RoutedEvent<ConnectionNavigationEventArgs> OpenConnectionsEvent =
        RoutedEvent.Register<ConnectionNavigationView, ConnectionNavigationEventArgs>(nameof(OpenConnections), RoutingStrategies.Bubble);

    public event EventHandler<ConnectionNavigationEventArgs>? OpenConnections
    {
        add => AddHandler(OpenConnectionsEvent, value);
        remove => RemoveHandler(OpenConnectionsEvent, value);
    }

    /// <summary>Asks the window to add a saved connection to the active script.</summary>
    public static readonly RoutedEvent<ConnectionNavigationEventArgs> AddToScriptEvent =
        RoutedEvent.Register<ConnectionNavigationView, ConnectionNavigationEventArgs>(nameof(AddToScript), RoutingStrategies.Bubble);

    public event EventHandler<ConnectionNavigationEventArgs>? AddToScript
    {
        add => AddHandler(AddToScriptEvent, value);
        remove => RemoveHandler(AddToScriptEvent, value);
    }

    /// <summary>The connection a drag from this tree carries, for a drop target outside it (the editor).</summary>
    public static string? DraggedConnectionName(IDataTransfer data)
        => data.TryGetValue(NodeFormat) is { } text && Decode(text) is { IsConnection: true } source ? source.Id : null;

    public ConnectionNavigationView()
    {
        InitializeComponent();
        _tree = this.FindControl<TreeView>("ConnectionTree")!;
        _tree.ContextMenu = new ContextMenu();
        _tree.ContextMenu.Opening += (_, _) => FillContextMenu(_tree.ContextMenu);
        _tree.AddHandler(PointerPressedEvent, Tree_PointerPressed, handledEventsToo: true);
        _tree.AddHandler(PointerMovedEvent, Tree_PointerMoved, handledEventsToo: true);
        DragDrop.SetAllowDrop(_tree, true);
        _tree.AddHandler(DragDrop.DragEnterEvent, Tree_DragOver);
        _tree.AddHandler(DragDrop.DragOverEvent, Tree_DragOver);
        _tree.AddHandler(DragDrop.DragLeaveEvent, (_, e) => { e.Handled = true; SetDropTarget(null, false); });
        _tree.AddHandler(DragDrop.DropEvent, Tree_Drop);
    }

    private ConnectionNavigationViewModel? ViewModel => DataContext as ConnectionNavigationViewModel;

    internal TreeView Tree => _tree;

    internal ConnectionNavigationNode? Selected
    {
        get => _tree.SelectedItem as ConnectionNavigationNode;
        set => _tree.SelectedItem = value;
    }

    // ── Keyboard and pointer ──────────────────────────────────────────────────

    private void ConnectionTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || Selected is not { } node || node.IsRenaming) return;
        switch (e.Key)
        {
            case Key.F2 when node.IsGroup && e.KeyModifiers == KeyModifiers.None:
                StartRename(node);
                break;
            case Key.Delete or Key.Back when node.IsGroup && e.KeyModifiers == KeyModifiers.None:
                DeleteGroup(vm, node);
                break;
            case Key.Enter when node.IsConnection:
                RaiseOpen(node.Id);
                break;
            case Key.Up or Key.Down when node.IsGroup && e.KeyModifiers == KeyModifiers.Alt:
                Reorder(vm, node, e.Key == Key.Up ? -1 : 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ConnectionTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (NodeAt(e.Source) is { IsConnection: true } node) RaiseOpen(node.Id);
    }

    private void Tree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_tree);
        var node = NodeAt(e.Source);
        var controlClick = OperatingSystem.IsMacOS()
            && e.KeyModifiers == KeyModifiers.Control
            && point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        if (point.Properties.IsRightButtonPressed || controlClick)
        {
            // A right click acts on the row under the pointer, or on the bundle itself on empty space.
            Selected = node;
            if (controlClick && node is not null)
                _tree.ContextMenu?.Open(_tree);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && node is { IsGroup: true })
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

        _possibleDragStart = point.Properties.IsLeftButtonPressed && node is { IsRenaming: false }
            ? (e.GetPosition(_tree), e, node) : null;
    }

    private void Tree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_possibleDragStart is not { } start || start.Args.Pointer.Id != e.Pointer.Id) return;
        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed) { _possibleDragStart = null; return; }
        var movement = e.GetPosition(_tree) - start.Position;
        if (Math.Abs(movement.X) <= MinimumDragDistance && Math.Abs(movement.Y) <= MinimumDragDistance) return;
        _possibleDragStart = null;
        _ = StartDragAsync(start.Args, start.Node);
    }

    private static async Task StartDragAsync(PointerPressedEventArgs args, ConnectionNavigationNode node)
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(NodeFormat, Encode(node));
        data.Add(item);
        // Move within the tree; Copy when dropped on the editor, which adds it to the script.
        await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void Tree_DragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var source = e.DataTransfer.TryGetValue(NodeFormat) is { } text ? Decode(text) : null;
        var target = DropGroupAt(e.Source);
        var valid = source is not null && CanDrop(source.Value, target);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(valid ? target : null, valid);
    }

    private void Tree_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = DropGroupAt(e.Source);
        SetDropTarget(null, false);
        if (ViewModel is not { } vm || e.DataTransfer.TryGetValue(NodeFormat) is not { } text || Decode(text) is not { } source
            || !CanDrop(source, target)) return;
        var targetId = target is { IsGroup: true } ? target.Id : null;
        if (source.IsConnection && vm.Find(source.Id, isConnection: true) is { } connection)
        {
            vm.MoveConnection(connection, targetId);
            Select(vm.Find(source.Id, isConnection: true));
        }
        else if (!source.IsConnection && vm.Find(source.Id, isConnection: false) is { } group)
        {
            vm.MoveGroup(group, targetId);
            Select(vm.Find(source.Id, isConnection: false));
        }
    }

    // Groups accept rows; a connection row stands for the group it is in, and a top-level connection
    // or empty space for the top level (null).
    private ConnectionNavigationNode? DropGroupAt(object? eventSource)
    {
        Visual? visual = eventSource as Visual;
        for (; visual is not null; visual = visual.GetVisualParent())
            if (visual is TreeViewItem { DataContext: ConnectionNavigationNode { IsConnection: false } group }) return group;
        return null;
    }

    private bool CanDrop((string Id, bool IsConnection) source, ConnectionNavigationNode? target)
    {
        if (ViewModel is not { CanEditGroups: true } vm) return false;
        if (source.IsConnection) return true;
        if (target is { IsGroup: true } && target.Id == source.Id) return false;
        return vm.Grouping.CanMoveGroup(source.Id, target is { IsGroup: true } ? target.Id : null);
    }

    private void SetDropTarget(ConnectionNavigationNode? node, bool valid)
    {
        if (node != _dropTarget)
        {
            _dropTarget?.IsDropTarget = false;
            node?.IsDropTarget = true;
            _dropTarget = node;
        }
        _tree.Background = valid && node is null && this.TryFindResource("Invoklet.Accent.BackgroundBrush", out var brush) && brush is IBrush accent
            ? accent : Brushes.Transparent;
    }

    private static string Encode(ConnectionNavigationNode node) => (node.IsConnection ? "c:" : "g:") + node.Id;

    private static (string Id, bool IsConnection)? Decode(string text) =>
        text.Length > 2 && text[1] == ':' && text[0] is 'c' or 'g' ? (text[2..], text[0] == 'c') : null;

    private static ConnectionNavigationNode? NodeAt(object? eventSource)
    {
        Visual? visual = eventSource as Visual;
        while (visual is not null and not TreeViewItem) visual = visual.GetVisualParent();
        return (visual as TreeViewItem)?.DataContext as ConnectionNavigationNode;
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    // The items are built once, like those of the menus declared in XAML, and each opening only
    // updates what they say and whether they show. Items created anew on every opening left the
    // popup, on Wayland, at the size of its first layout before they were styled, which cut off its
    // sides and rounded corners.
    private readonly List<Control> _menuItems = [];
    private MenuItem _addToScript = null!, _refreshConnection = null!, _editConnection = null!, _newGroup = null!,
        _renameGroup = null!, _deleteGroup = null!, _moveUp = null!, _moveDown = null!, _moveTo = null!, _refreshAll = null!;
    private Separator _connectionSeparator = null!;
    private ConnectionNavigationNode? _menuNode;

    private void BuildContextMenu(ContextMenu menu)
    {
        _addToScript = Item("Add to Script", "Icon.ConnectionAttached", true,
            () => OnMenuNode(node => RaiseEvent(new ConnectionNavigationEventArgs(AddToScriptEvent, node.Id))));
        _refreshConnection = Item("Refresh", "Icon.Sync", true, () => OnMenuNode(node => _ = ViewModel?.RefreshConnectionAsync(node)));
        _editConnection = Item("Edit Connection…", "Icon.Rename", true, () => OnMenuNode(node => RaiseOpen(node.Id)));
        _connectionSeparator = new Separator();
        _newGroup = Item("New Group", "Icon.Plus", true, () =>
        {
            if (ViewModel is { } vm) CreateGroup(vm, _menuNode is { IsGroup: true } ? _menuNode : null);
        });
        _renameGroup = Item("Rename", "Icon.Rename", true, () => OnMenuNode(StartRename));
        _deleteGroup = Item("Delete Group", "Icon.Delete", true, () => OnMenuNode(node => { if (ViewModel is { } vm) DeleteGroup(vm, node); }));
        _moveUp = Item("Move Up", null, true, () => OnMenuNode(node => { if (ViewModel is { } vm) Reorder(vm, node, -1); }));
        _moveDown = Item("Move Down", null, true, () => OnMenuNode(node => { if (ViewModel is { } vm) Reorder(vm, node, 1); }));
        _moveTo = new MenuItem { Header = "Move To" };
        _refreshAll = Item("Refresh All", "Icon.Sync", true, () => _ = ViewModel?.RefreshAllAsync());
        var manage = Item("Manage Connections…", "Icon.Settings", true, () => RaiseOpen(null));
        _menuItems.AddRange([_addToScript, _refreshConnection, _editConnection, _connectionSeparator, _newGroup,
            _renameGroup, _deleteGroup, _moveUp, _moveDown, _moveTo, new Separator(), _refreshAll, manage]);
        foreach (var item in _menuItems) menu.Items.Add(item);
    }

    private void OnMenuNode(Action<ConnectionNavigationNode> action)
    {
        if (_menuNode is { } node) action(node);
    }

    internal void FillContextMenu(ContextMenu menu)
    {
        if (_menuItems.Count == 0) BuildContextMenu(menu);
        var vm = ViewModel;
        foreach (var item in _menuItems) item.IsVisible = vm is not null;
        if (vm is null) return;
        var node = _menuNode = Selected;
        var editable = vm.CanEditGroups;

        var isConnection = node is { IsConnection: true };
        foreach (var item in new Control[] { _addToScript, _refreshConnection, _editConnection, _connectionSeparator })
            item.IsVisible = isConnection;
        if (isConnection)
        {
            _addToScript.Header = node!.IsAttached ? "In This Script" : "Add to Script";
            _addToScript.IsEnabled = vm.CanAddToScript(node);
            _refreshConnection.Header = node.HasContract ? "Refresh Contract" : "Refresh";
            _refreshConnection.IsEnabled = !node.IsLoading;
        }

        var isGroup = node is { IsGroup: true };
        _newGroup.Header = isGroup ? "New Subgroup" : "New Group";
        _newGroup.IsEnabled = editable && (!isGroup || vm.Grouping.DepthOf(node!.Id) < Services.Connections.ConnectionGroupingModel.MaxDepth);
        foreach (var item in new[] { _renameGroup, _deleteGroup, _moveUp, _moveDown })
        {
            item.IsVisible = isGroup;
            item.IsEnabled = editable;
        }

        _moveTo.IsVisible = node is not null;
        _moveTo.Items.Clear();
        if (node is not null)
        {
            var current = node.IsConnection ? vm.Grouping.GroupFor(node.Id) : vm.Grouping.Find(node.Id)?.ParentId;
            foreach (var target in vm.MoveTargets(node))
            {
                var isCurrent = target.GroupId == current || (target.GroupId is null && current == Services.Connections.ConnectionGroupingModel.UngroupedId);
                var targetId = target.GroupId;
                _moveTo.Items.Add(Item(new string(' ', Math.Max(0, target.Depth - 1) * 3) + target.Path.Split(" / ")[^1], null, !isCurrent, () =>
                {
                    if (node.IsConnection) vm.MoveConnection(node, targetId);
                    else vm.MoveGroup(node, targetId);
                    Select(vm.Find(node.Id, node.IsConnection));
                }, target.Path));
            }
            _moveTo.IsEnabled = editable && _moveTo.Items.Count > 1;
        }
        _refreshAll.IsEnabled = vm.IsLoaded && !vm.IsLoading;
    }

    private MenuItem Item(string header, string? iconKey, bool enabled, Action action, string? accessibleName = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (accessibleName is not null) global::Avalonia.Automation.AutomationProperties.SetName(item, accessibleName);
        if (iconKey is not null && (this.TryFindResource(iconKey, out var icon) || Application.Current?.TryFindResource(iconKey, out icon) == true)
            && icon is Geometry data)
            item.Icon = new PathIcon { Data = data, Width = 16, Height = 16 };
        item.Click += (_, _) => action();
        return item;
    }

    private void ManageConnections_Click(object? sender, RoutedEventArgs e) => RaiseOpen(null);

    private void DismissNotice_Click(object? sender, RoutedEventArgs e) => ViewModel?.DismissNotice();

    // ── Actions ───────────────────────────────────────────────────────────────

    private void RaiseOpen(string? connectionName) => RaiseEvent(new ConnectionNavigationEventArgs(OpenConnectionsEvent, connectionName));

    internal void CreateGroup(ConnectionNavigationViewModel vm, ConnectionNavigationNode? parent)
    {
        if (vm.CreateGroup(parent) is { } created)
        {
            Select(created);
            StartRename(created);
        }
    }

    private void DeleteGroup(ConnectionNavigationViewModel vm, ConnectionNavigationNode node)
    {
        var parentId = vm.Grouping.Find(node.Id)?.ParentId;
        if (vm.DeleteGroup(node))
            Select(parentId is null ? null : vm.Find(parentId, isConnection: false));
    }

    private void Reorder(ConnectionNavigationViewModel vm, ConnectionNavigationNode node, int direction)
    {
        if (vm.ReorderGroup(node, direction)) Select(vm.Find(node.Id, isConnection: false));
    }

    // Selection survives the rebuild after a change, and keyboard focus stays in the tree.
    private void Select(ConnectionNavigationNode? node)
    {
        Selected = node;
        if (node is null) { _tree.Focus(); return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_tree.TreeContainerFromItem(node) is TreeViewItem item) item.Focus();
        }, DispatcherPriority.Loaded);
    }

    internal void StartRename(ConnectionNavigationNode node)
    {
        if (!node.IsGroup || ViewModel is not { CanEditGroups: true }) return;
        node.EditingName = node.Name;
        node.IsRenaming = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_tree.TreeContainerFromItem(node) is not TreeViewItem item) return;
            var box = item.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible && b.DataContext == node);
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void CommitRename(ConnectionNavigationNode node)
    {
        if (!node.IsRenaming) return;
        node.IsRenaming = false;
        if (ViewModel is { } vm && node.EditingName.Trim() is { Length: > 0 } name && name != node.Name && vm.RenameGroup(node, name))
            Select(vm.Find(node.Id, isConnection: false));
        else
            Select(node);
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ConnectionNavigationNode node }) return;
        if (e.Key == Key.Enter) { CommitRename(node); e.Handled = true; }
        else if (e.Key == Key.Escape) { node.IsRenaming = false; Select(node); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ConnectionNavigationNode { IsRenaming: true } node }) CommitRename(node);
    }
}
