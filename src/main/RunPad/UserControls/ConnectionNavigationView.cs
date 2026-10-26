using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RunPad.UserControls.Models;

namespace RunPad.UserControls;

/// <summary>Asks the window to open the Connections editor, at a connection when there is one.</summary>
public sealed class ConnectionNavigationEventArgs(RoutedEvent routedEvent, string? connectionName) : RoutedEventArgs(routedEvent)
{
    public string? ConnectionName { get; } = connectionName;
}

/// <summary>
/// The sidebar's Connections tree. Keyboard: F2 renames a group, Delete removes one (its contents
/// move up), Enter opens a connection, Alt+Up/Down reorders a group, and the context menu
/// (Shift+F10 or the menu key) holds every action, including Move to for keyboard moves. Rows can
/// also be dragged onto a group, or onto a top-level connection or empty space for the top level.
/// </summary>
public partial class ConnectionNavigationView : UserControl
{
    internal static readonly DataFormat<string> NodeFormat =
        DataFormat.CreateStringApplicationFormat("runpad-connection-node");

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
        if (point.Properties.IsRightButtonPressed)
        {
            // A right click acts on the row under the pointer, or on the bundle itself on empty space.
            Selected = node;
            return;
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
        await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
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
        _tree.Background = valid && node is null && this.TryFindResource("RunPad.Accent.BackgroundBrush", out var brush) && brush is IBrush accent
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

    internal void FillContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (ViewModel is not { } vm) return;
        var node = Selected;
        var editable = vm.CanEditGroups;

        if (node is { IsConnection: true })
            menu.Items.Add(Item("Edit Connection…", "Icon.Settings", true, () => RaiseOpen(node.Id)));
        menu.Items.Add(Item(node is { IsGroup: true } ? "New Subgroup" : "New Group", "Icon.Plus",
            editable && (node is not { IsGroup: true } || vm.Grouping.DepthOf(node.Id) < Services.Connections.ConnectionGroupingModel.MaxDepth),
            () => CreateGroup(vm, node is { IsGroup: true } ? node : null)));
        if (node is { IsGroup: true })
        {
            menu.Items.Add(Item("Rename", "Icon.Rename", editable, () => StartRename(node)));
            menu.Items.Add(Item("Delete Group", "Icon.Delete", editable, () => DeleteGroup(vm, node)));
            menu.Items.Add(Item("Move Up", null, editable, () => Reorder(vm, node, -1)));
            menu.Items.Add(Item("Move Down", null, editable, () => Reorder(vm, node, 1)));
        }
        if (node is not null)
        {
            var move = new MenuItem { Header = "Move To", IsEnabled = editable };
            var current = node.IsConnection ? vm.Grouping.GroupFor(node.Id) : vm.Grouping.Find(node.Id)?.ParentId;
            foreach (var target in vm.MoveTargets(node))
            {
                var isCurrent = target.GroupId == current || (target.GroupId is null && current == Services.Connections.ConnectionGroupingModel.UngroupedId);
                var targetId = target.GroupId;
                move.Items.Add(Item(new string(' ', Math.Max(0, target.Depth - 1) * 3) + target.Path.Split(" / ")[^1], null, !isCurrent, () =>
                {
                    if (node.IsConnection) vm.MoveConnection(node, targetId);
                    else vm.MoveGroup(node, targetId);
                    Select(vm.Find(node.Id, node.IsConnection));
                }, target.Path));
            }
            move.IsEnabled = editable && move.Items.Count > 1;
            menu.Items.Add(move);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Manage Connections…", "Icon.NavigationConnections", true, () => RaiseOpen(null)));
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
