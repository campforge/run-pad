using System.Collections.ObjectModel;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Services.Connections;
using Invoklet.Services.Secrets;
using Invoklet.Windows.Models;
using ReactiveUI;

namespace Invoklet.UserControls.Models;

public enum ConnectionNavigationNodeKind { Group, Connection }

/// <summary>What is known about a connection without connecting to it.</summary>
public enum ConnectionRowState
{
    /// <summary>Nothing against it.</summary>
    Ready,
    /// <summary>Its contract is being read.</summary>
    Loading,
    /// <summary>The contract read earlier is still used, but the last refresh failed.</summary>
    Stale,
    /// <summary>It can't be used as it is: no driver, settings the driver rejects, a missing secret or variable, no readable contract.</summary>
    Problem,
}

public readonly record struct ConnectionStatus(ConnectionRowState State, string? Text = null)
{
    public static readonly ConnectionStatus Ready = new(ConnectionRowState.Ready);
}

/// <summary>A row of the sidebar's Connections tree: a user group or a connection.</summary>
public sealed class ConnectionNavigationNode : ReactiveObject
{
    private ConnectionNavigationNode(ConnectionNavigationNodeKind kind, string id, string name, int depth)
    {
        Kind = kind;
        Id = id;
        Name = name;
        Depth = depth;
        if (kind != ConnectionNavigationNodeKind.Connection) Children = [];
    }

    internal static ConnectionNavigationNode Group(ConnectionGroup group, int depth) =>
        new(ConnectionNavigationNodeKind.Group, group.Id, group.Name, depth) { IconKey = "Icon.ConnectionGroup" };

    internal static ConnectionNavigationNode Connection(MdRunConnection connection, IConnectionDriver? driver, int depth, ConnectionStatus status)
    {
        var node = new ConnectionNavigationNode(ConnectionNavigationNodeKind.Connection, connection.Name, connection.Name, depth)
        {
            DriverId = connection.Driver,
            DriverName = driver?.DisplayName ?? $"{connection.Driver} (not available in this Invoklet)",
            IsDriverMissing = driver is null,
            HasContract = driver is IConnectionSchemaDriver,
            IconKey = DriverIconKey(connection.Driver),
        };
        node.Status = status;
        return node;
    }

    public ConnectionNavigationNodeKind Kind { get; }

    /// <summary>The group's ID, or the connection's name (its key in the connection list).</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>0 for the top level of the Connections bundle.</summary>
    public int Depth { get; }

    public string? DriverId { get; private init; }
    public string? DriverName { get; private init; }
    public bool IsDriverMissing { get; private init; }

    /// <summary>Connection row: its driver reads a contract (gRPC), which Refresh reads again.</summary>
    public bool HasContract { get; private init; }

    /// <summary>Connection row: what is known about it; changes as contracts are read.</summary>
    public ConnectionStatus Status
    {
        get;
        internal set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            foreach (var name in new[] { nameof(Problem), nameof(HasProblem), nameof(IsLoading), nameof(IsStale), nameof(StatusText),
                         nameof(ShowsProblem), nameof(AccessibleName), nameof(ToolTip) })
                this.RaisePropertyChanged(name);
        }
    }

    public string? StatusText => Status.Text;

    /// <summary>Connection row: why it can't be used as it is; null when nothing is known against it.</summary>
    public string? Problem => Status.State == ConnectionRowState.Problem ? Status.Text : null;

    public bool HasProblem => Status.State == ConnectionRowState.Problem;
    public bool IsLoading => Status.State == ConnectionRowState.Loading;
    public bool IsStale => Status.State == ConnectionRowState.Stale;

    /// <summary>Group row: a connection somewhere inside has a problem, so a folded group still shows it.</summary>
    public bool HasProblemInside => Children?.Any(c => c.HasProblem || c.HasProblemInside) == true;

    public bool ShowsProblem => HasProblem || HasProblemInside;

    /// <summary>Resource key of the row's icon in Resources/Icons.axaml.</summary>
    public string IconKey { get; private init; } = "Icon.DriverDatabase";

    /// <summary>Null for a connection row.</summary>
    public ObservableCollection<ConnectionNavigationNode>? Children { get; }

    public bool IsConnection => Kind == ConnectionNavigationNodeKind.Connection;
    public bool IsGroup => Kind == ConnectionNavigationNodeKind.Group;

    /// <summary>Connection row: the active script uses this connection.</summary>
    public bool IsAttached
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(AccessibleName));
            this.RaisePropertyChanged(nameof(ToolTip));
        }
    }

    public bool IsExpanded
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ExpandedChanged?.Invoke(this);
        }
    }

    internal event Action<ConnectionNavigationNode>? ExpandedChanged;

    /// <summary>The group a dragged row would be dropped into.</summary>
    public bool IsDropTarget
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasChildrenCount => !IsConnection;

    public bool IsRenaming
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string EditingName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? "");
    } = "";

    public int ConnectionCount => Children?.Sum(c => c.IsConnection ? 1 : c.ConnectionCount) ?? 0;

    // The states are spoken as words, not only shown as badges, so color or a glyph alone never carries them.
    public string AccessibleName => Kind switch
    {
        ConnectionNavigationNodeKind.Connection => string.Join(", ", new[]
        {
            Name, DriverName, IsAttached ? "used by this script" : null,
            Status.State switch
            {
                ConnectionRowState.Problem => "problem: " + Status.Text,
                ConnectionRowState.Stale => "out of date: " + Status.Text,
                ConnectionRowState.Loading => "reading its contract",
                _ => null,
            },
        }.Where(s => s is not null)),
        _ => $"{Name} group, {ConnectionCount} {(ConnectionCount == 1 ? "connection" : "connections")}"
             + (HasProblemInside ? ", has connections with problems" : ""),
    };

    public string ToolTip => IsConnection
        ? string.Join("\n", new[] { DriverName, IsAttached ? "Used by this script" : null, StatusText }.Where(s => s is not null))
        : AccessibleName;

    internal void RefreshCounts()
    {
        foreach (var child in Children ?? []) child.RefreshCounts();
        this.RaisePropertyChanged(nameof(ConnectionCount));
        this.RaisePropertyChanged(nameof(HasProblemInside));
        this.RaisePropertyChanged(nameof(ShowsProblem));
        this.RaisePropertyChanged(nameof(AccessibleName));
        this.RaisePropertyChanged(nameof(ToolTip));
    }

    internal static string DriverIconKey(string driverId) => driverId.ToLowerInvariant() switch
    {
        "http" or "https" or "rest" => "Icon.DriverHttp",
        "grpc" => "Icon.DriverGrpc",
        _ => "Icon.DriverDatabase",
    };

    public override string ToString() => AccessibleName;
}

/// <summary>A place a row can be moved to, for the tree's Move to menu.</summary>
public sealed record ConnectionMoveTarget(string? GroupId, string Path, int Depth);

/// <summary>
/// The Connections bundle of the sidebar: the reusable connections organized into the user's groups.
/// Connections outside any group sit at the top level, after the groups. Groups live next to the
/// connection list and never in a script; which connections a script runs with stays in its own front
/// matter, shown here only as the attached badge.
/// </summary>
public sealed class ConnectionNavigationViewModel : ReactiveObject, IDisposable
{
    private readonly IConnectionStore _store;
    private readonly ConnectionDriverRegistry? _drivers;
    private readonly FileConnectionGroupingStore _groupStore;
    private readonly ISecretStore? _secrets;
    private readonly ConnectionSchemas? _schemas;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private ConnectionGroupingModel _grouping = new();
    private IReadOnlyList<MdRunConnection> _connections = [];
    private IReadOnlyDictionary<string, ConnectionStatus> _statuses = new Dictionary<string, ConnectionStatus>();
    private readonly SynchronizationContext? _context;
    private int _statusGeneration;
    private int _statusRefreshQueued;
    private HashSet<string> _attached = new(StringComparer.Ordinal);
    private bool _groupingUnreadable;
    private int _refreshGeneration;
    private CancellationTokenSource? _operationCts;
    private readonly object _operationGate = new();
    private bool _disposed;
    private readonly Action<string>? _schemaChangedHandler;
    private readonly Action<string>? _schemaFetchingChangedHandler;

    public ConnectionNavigationViewModel(IConnectionStore store, ConnectionDriverRegistry? drivers = null,
        FileConnectionGroupingStore? groupStore = null, ISecretStore? secrets = null, ConnectionSchemas? schemas = null)
    {
        _store = store;
        _drivers = drivers;
        _secrets = secrets;
        _schemas = schemas;
        _groupStore = groupStore ?? new FileConnectionGroupingStore(store.FilePath);
        Nodes.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
        // Contracts are read by the editor, runs and the Connections window too: the rows follow.
        _context = SynchronizationContext.Current;
        if (schemas is not null)
        {
            _schemaChangedHandler = _ => QueueStatusRefresh();
            _schemaFetchingChangedHandler = _ => QueueStatusRefresh();
            schemas.Cache.Changed += _schemaChangedHandler;
            schemas.Cache.FetchingChanged += _schemaFetchingChangedHandler;
        }
    }

    public ObservableCollection<ConnectionNavigationNode> Nodes { get; } = [];

    /// <summary>Why the list or its groups could not be read or written; null when all is well.</summary>
    public string? Error
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    /// <summary>A rejected organization change, such as nesting deeper than allowed.</summary>
    public string? Notice
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => Notice is not null;

    public bool IsLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsLoaded
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(IsEmpty)); }
    }

    /// <summary>Loaded, readable, and with neither a connection nor a group to show.</summary>
    public bool IsEmpty => IsLoaded && !HasError && _connections.Count == 0 && _grouping.Groups.Count == 0;

    public ConnectionGroupingModel Grouping => _grouping;

    /// <summary>
    /// Reads the connection list and its groups off the UI thread and rebuilds the tree. A newer
    /// refresh wins over one still running; the tree keeps its last contents while reading.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;
        IsLoading = true;
        try
        {
            var (connections, grouping, error, groupingUnreadable) = await Task.Run(Load).ConfigureAwait(true);
            var statuses = await Task.Run(() => FindStatuses(connections)).ConfigureAwait(true);
            if (generation != _refreshGeneration) return;
            _connections = connections;
            _statuses = statuses;
            _grouping = grouping;
            _groupingUnreadable = groupingUnreadable;
            Error = error;
            Notice = null;
            Rebuild();
            IsLoaded = true;
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            if (generation == _refreshGeneration) IsLoading = false;
        }
    }

    private (IReadOnlyList<MdRunConnection>, ConnectionGroupingModel, string?, bool) Load()
    {
        IReadOnlyList<MdRunConnection> connections;
        try { connections = _store.LoadSnapshot().Connections; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ([], new ConnectionGroupingModel(), $"The connection list could not be read. {ex.Message}", false);
        }
        try { return (connections, new ConnectionGroupingModel(_groupStore.Load()), null, false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The connections are still shown, all at the top level; the unreadable file is left as it is.
            return (connections, new ConnectionGroupingModel(), $"The connection groups could not be read, so they are not shown and cannot be changed. {ex.Message}", true);
        }
    }

    /// <summary>A script is active, so connections can be added to it.</summary>
    public bool HasActiveScript
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // What can be known without connecting: the driver, its own validation of the settings, secrets
    // and environment variables the settings name, and the state of the contract (gRPC). Only names
    // are looked up, never a secret's value. Each connection is checked on its own, so one driver
    // throwing doesn't hide the others.
    private Dictionary<string, ConnectionStatus> FindStatuses(IReadOnlyList<MdRunConnection> connections)
    {
        HashSet<string>? secretNames = null;
        if (_secrets is not null)
        {
            try { secretNames = _secrets.LoadSnapshot().Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        var statuses = new Dictionary<string, ConnectionStatus>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            try { statuses[connection.Name] = StatusOf(connection, secretNames); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                statuses[connection.Name] = new(ConnectionRowState.Problem, $"Its settings couldn't be checked: {ex.Message}");
            }
        }
        return statuses;
    }

    private ConnectionStatus StatusOf(MdRunConnection connection, HashSet<string>? secretNames)
    {
        if (_drivers is null) return ConnectionStatus.Ready;
        if (_drivers.Find(connection.Driver) is not { } driver)
            return new(ConnectionRowState.Problem, $"This Invoklet has no '{connection.Driver}' driver.");
        if (driver.Validate(connection).FirstOrDefault() is { } invalid) return new(ConnectionRowState.Problem, invalid);
        if (secretNames is not null)
        {
            var placeholders = ConnectionPlaceholderResolver.Resolve(connection.Params,
                name => secretNames.Contains(name) ? "" : null,
                name => Environment.GetEnvironmentVariable(name) is null ? null : "");
            if (placeholders.Errors.FirstOrDefault() is { } missing) return new(ConnectionRowState.Problem, missing);
        }
        if (driver is IConnectionSchemaDriver && _schemas?.Cache is { } cache)
        {
            if (cache.IsFetching(connection)) return new(ConnectionRowState.Loading, "Reading its contract…");
            if (cache.ErrorFor(connection) is { } error)
                return cache.Get(connection) is null
                    ? new(ConnectionRowState.Problem, $"Its contract couldn't be read: {error}")
                    : new(ConnectionRowState.Stale, $"Using the contract read earlier; the last refresh failed: {error}");
        }
        return ConnectionStatus.Ready;
    }

    // Several schema events often come at once (a read starting, ending, writing): one recheck runs
    // for them, on the UI thread the tree was made on.
    private void QueueStatusRefresh()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _statusRefreshQueued, 1) == 1) return;
        void Run(object? _)
        {
            Interlocked.Exchange(ref _statusRefreshQueued, 0);
            if (_disposed) return;
            _ = RefreshStatusesAsync();
        }
        if (_context is { } context) context.Post(Run, null);
        else Run(null);
    }

    /// <summary>Checks every connection's status again without reading the list, and updates the rows.</summary>
    public async Task RefreshStatusesAsync()
    {
        if (_disposed) return;
        var generation = ++_statusGeneration;
        var connections = _connections;
        var statuses = await Task.Run(() => FindStatuses(connections)).ConfigureAwait(true);
        if (generation != _statusGeneration || !ReferenceEquals(connections, _connections)) return;
        if (_disposed) return;
        _statuses = statuses;
        foreach (var node in AllNodes().Where(n => n.IsConnection))
            node.Status = statuses.GetValueOrDefault(node.Id, ConnectionStatus.Ready);
        foreach (var root in Nodes) root.RefreshCounts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancellationTokenSource? operation;
        lock (_operationGate)
            operation = Interlocked.Exchange(ref _operationCts, null);
        operation?.Cancel();
        Interlocked.Increment(ref _statusGeneration);
        if (_schemas is not null)
        {
            if (_schemaChangedHandler is not null) _schemas.Cache.Changed -= _schemaChangedHandler;
            if (_schemaFetchingChangedHandler is not null) _schemas.Cache.FetchingChanged -= _schemaFetchingChangedHandler;
        }
        Interlocked.Exchange(ref _statusRefreshQueued, 0);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Refresh on a connection: reads its contract again when its driver has one (gRPC), then checks
    /// its status; for any other connection, reads the list again and checks every status.
    /// </summary>
    public async Task RefreshConnectionAsync(ConnectionNavigationNode node)
    {
        if (_disposed || !node.IsConnection) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            if (node.HasContract && _schemas is { } schemas && _connections.FirstOrDefault(c => c.Name == node.Id) is { } connection)
            {
                await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.All, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed) return;
                await RefreshStatusesAsync().ConfigureAwait(true);
                return;
            }
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndOperation(operation); }
    }

    /// <summary>Reads the list again and every contract (gRPC) too, then checks every status.</summary>
    public async Task RefreshAllAsync()
    {
        if (_disposed) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            await RefreshAsync().ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed) return;
            if (_schemas is { } schemas && _drivers is { } drivers)
            {
                var withContracts = _connections.Where(c => drivers.Find(c.Driver) is IConnectionSchemaDriver).ToList();
                if (withContracts.Count > 0)
                    await schemas.EnsureAsync(withContracts, null, ConnectionSchemaRefresh.All, token).ConfigureAwait(true);
            }
            if (token.IsCancellationRequested || _disposed) return;
            await RefreshStatusesAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndOperation(operation); }
    }

    private CancellationTokenSource BeginOperation()
    {
        var current = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_operationGate)
        {
            if (_disposed)
            {
                current.Cancel();
                return current;
            }
            previous = Interlocked.Exchange(ref _operationCts, current);
        }
        previous?.Cancel();
        return current;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        lock (_operationGate)
        {
            if (ReferenceEquals(_operationCts, operation))
                Interlocked.CompareExchange(ref _operationCts, null, operation);
        }
        operation.Dispose();
    }

    /// <summary>Marks the connections the active script uses (null: no script is active).</summary>
    public void SetAttached(IEnumerable<MdRunConnection>? scriptConnections)
    {
        HasActiveScript = scriptConnections is not null;
        _attached = (scriptConnections ?? []).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var node in AllNodes().Where(n => n.IsConnection)) node.IsAttached = _attached.Contains(node.Id);
    }

    public bool CanEditGroups => IsLoaded && !_groupingUnreadable && Error is null;

    /// <summary>The folded groups' IDs changed: what to keep for the next start.</summary>
    public event Action<IReadOnlyCollection<string>>? CollapsedGroupsChanged;

    /// <summary>Folds the groups with these IDs (kept from the previous run) when the tree is built.</summary>
    public void RestoreCollapsedGroups(IEnumerable<string>? groupIds)
    {
        _collapsed.Clear();
        foreach (var id in groupIds ?? []) _collapsed.Add(id);
        foreach (var node in AllNodes().Where(n => n.IsGroup)) node.IsExpanded = !_collapsed.Contains(node.Id);
    }

    public IReadOnlyCollection<string> CollapsedGroups => [.. _collapsed];

    /// <summary>Adds a group at the top level or inside <paramref name="parent"/>; null when it is not allowed.</summary>
    public ConnectionNavigationNode? CreateGroup(ConnectionNavigationNode? parent, string name = "New Group")
    {
        if (!CanEditGroups) return null;
        var parentId = parent is { IsGroup: true } ? parent.Id : null;
        ConnectionGroup group;
        try { group = _grouping.Create(UniqueName(parentId, name), parentId); }
        catch (InvalidOperationException ex) { Notice = ex.Message; return null; }
        if (!Persist()) return null;
        if (parentId is not null) Unfold(parentId);
        Rebuild();
        return Find(group.Id, isConnection: false);
    }

    public bool RenameGroup(ConnectionNavigationNode node, string name)
    {
        if (!CanEditGroups || !node.IsGroup) return false;
        if (string.IsNullOrWhiteSpace(name)) { Notice = "A group needs a name."; return false; }
        if (_grouping.Find(node.Id)?.Name == name.Trim()) return true;
        _grouping.Rename(node.Id, name);
        return PersistAndRebuild();
    }

    /// <summary>Removes the group only: what was in it moves up a level.</summary>
    public bool DeleteGroup(ConnectionNavigationNode node)
    {
        if (!CanEditGroups || !node.IsGroup) return false;
        _grouping.Delete(node.Id);
        if (_collapsed.Remove(node.Id)) CollapsedGroupsChanged?.Invoke(CollapsedGroups);
        return PersistAndRebuild();
    }

    public bool MoveConnection(ConnectionNavigationNode connection, string? groupId)
    {
        if (!CanEditGroups || !connection.IsConnection) return false;
        if (_grouping.GroupFor(connection.Id) == (groupId ?? ConnectionGroupingModel.UngroupedId)) return true;
        _grouping.MoveConnection(connection.Id, groupId);
        if (groupId is not null) Unfold(groupId);
        return PersistAndRebuild();
    }

    public bool MoveGroup(ConnectionNavigationNode group, string? parentId)
    {
        if (!CanEditGroups || !group.IsGroup) return false;
        if (parentId == ConnectionGroupingModel.UngroupedId) parentId = null;
        try { _grouping.MoveGroup(group.Id, parentId); }
        catch (InvalidOperationException ex) { Notice = ex.Message; return false; }
        if (parentId is not null) Unfold(parentId);
        return PersistAndRebuild();
    }

    /// <summary>Moves a group one place up (-1) or down (+1) among its siblings.</summary>
    public bool ReorderGroup(ConnectionNavigationNode group, int direction)
    {
        if (!CanEditGroups || !group.IsGroup || !_grouping.Reorder(group.Id, direction)) return false;
        return PersistAndRebuild();
    }

    /// <summary>
    /// Where a row may go, in tree order: the top level, then every group; for a group, not itself,
    /// its own groups or a place too deep.
    /// </summary>
    public IReadOnlyList<ConnectionMoveTarget> MoveTargets(ConnectionNavigationNode node)
    {
        var targets = new List<ConnectionMoveTarget>();
        if (!CanEditGroups) return targets;
        targets.Add(new(null, "Top Level", 0));
        void Walk(string? parentId, string prefix, int depth)
        {
            foreach (var group in _grouping.ChildrenOf(parentId))
            {
                var path = prefix.Length == 0 ? group.Name : $"{prefix} / {group.Name}";
                if (node.IsGroup && group.Id == node.Id) continue;
                if (node.IsConnection || _grouping.CanMoveGroup(node.Id, group.Id)) targets.Add(new(group.Id, path, depth + 1));
                Walk(group.Id, path, depth + 1);
            }
        }
        Walk(null, "", 0);
        return targets;
    }

    /// <summary>Whether the connection can be offered to the active script: there is one, it doesn't use it yet, and this Invoklet has its driver.</summary>
    public bool CanAddToScript(ConnectionNavigationNode node)
        => HasActiveScript && node is { IsConnection: true, IsAttached: false, IsDriverMissing: false };

    public void DismissNotice() => Notice = null;

    /// <summary>
    /// Adds a saved connection to the script's front matter with the same checks as Dependencies →
    /// Connections (driver, readable contract, a name that doesn't clash in code). When its driver
    /// needs NuGet packages the script doesn't have, <paramref name="confirmPackages"/> is asked first
    /// and the packages are added with it. True when the front matter changed; otherwise
    /// <see cref="Notice"/> says why, or the user declined.
    /// </summary>
    public async Task<bool> AddToScriptAsync(MdRunFrontMatter frontMatter, string? scriptPath, string connectionName,
        Func<string, Task<bool>> confirmPackages)
    {
        if (_disposed) return false;
        Notice = null;
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, package) in frontMatter.Packages ?? []) packages[id] = package.Version;
        var script = new ScriptConnectionsViewModel(_store, _secrets, _drivers, _schemas);
        script.UsePackages(() => packages.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), (id, version) => packages[id] = version);
        script.SetScriptPath(scriptPath);
        script.Load(frontMatter);
        if (script.ReadError is { } readError) { Notice = readError; return false; }

        script.RequestAdd(connectionName);
        if (script.IsAddConfirmationPending)
        {
            if (!await confirmPackages(script.AddMessage!).ConfigureAwait(true))
            {
                script.CancelPendingAdd();
                return false;
            }
            // The confirmation belongs to the window that owns this view model. If that window
            // closed while the dialog was open, do not commit the staged change to the caller's
            // front matter or update the disposed navigation tree.
            if (_disposed) return false;
            script.ConfirmPendingAdd();
        }
        if (_disposed) return false;
        if (script.CreateMessage is { } message) { Notice = message; return false; }
        if (!script.HasChanges) return false;

        frontMatter.Connections = script.ToList();
        var scriptPackages = frontMatter.Packages ??= [];
        foreach (var (id, version) in packages)
            if (!scriptPackages.Keys.Any(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)))
                scriptPackages[id] = new Package(version);
        SetAttached(frontMatter.Connections);
        return true;
    }

    internal IEnumerable<ConnectionNavigationNode> AllNodes()
    {
        var stack = new Stack<ConnectionNavigationNode>(Nodes.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in (node.Children ?? []).Reverse()) stack.Push(child);
        }
    }

    public ConnectionNavigationNode? Find(string id, bool isConnection) =>
        AllNodes().FirstOrDefault(n => n.Id == id && n.IsConnection == isConnection);

    private bool PersistAndRebuild()
    {
        var saved = Persist();
        Rebuild();
        return saved;
    }

    private bool Persist()
    {
        try
        {
            _groupStore.Save(_grouping.Snapshot());
            Notice = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"The connection groups could not be saved. {ex.Message}";
            return false;
        }
    }

    private string UniqueName(string? parentId, string name)
    {
        var taken = _grouping.ChildrenOf(parentId).Select(g => g.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var candidate = name.Trim();
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{name.Trim()} {i}";
        return candidate;
    }

    // Rebuilds the rows from the list and the groups. Expansion is kept by group ID across rebuilds
    // and refreshes; a connection whose group no longer exists lands at the top level.
    private void Rebuild()
    {
        var byGroup = _connections
            .GroupBy(c => _grouping.GroupFor(c.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        ConnectionNavigationNode Build(ConnectionGroup group, int depth)
        {
            var node = ConnectionNavigationNode.Group(group, depth);
            foreach (var child in _grouping.ChildrenOf(group.Id)) node.Children!.Add(Build(child, depth + 1));
            foreach (var connection in byGroup.GetValueOrDefault(group.Id) ?? []) node.Children!.Add(ConnectionRow(connection, depth + 1));
            return node;
        }

        var roots = _grouping.ChildrenOf(null).Select(g => Build(g, 0)).ToList();
        roots.AddRange((byGroup.GetValueOrDefault(ConnectionGroupingModel.UngroupedId) ?? []).Select(c => ConnectionRow(c, 0)));

        Nodes.Clear();
        foreach (var root in roots)
        {
            root.RefreshCounts();
            Nodes.Add(root);
        }
        foreach (var node in AllNodes().Where(n => !n.IsConnection))
        {
            node.IsExpanded = !_collapsed.Contains(node.Id);
            node.ExpandedChanged += OnExpandedChanged;
        }
        this.RaisePropertyChanged(nameof(CanEditGroups));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    private ConnectionNavigationNode ConnectionRow(MdRunConnection connection, int depth)
    {
        var node = ConnectionNavigationNode.Connection(connection, _drivers?.Find(connection.Driver), depth,
            _statuses.GetValueOrDefault(connection.Name, ConnectionStatus.Ready));
        node.IsAttached = _attached.Contains(connection.Name);
        return node;
    }

    // A group something was just put into opens, so the moved row stays in sight.
    private void Unfold(string groupId)
    {
        if (_collapsed.Remove(groupId)) CollapsedGroupsChanged?.Invoke(CollapsedGroups);
    }

    private void OnExpandedChanged(ConnectionNavigationNode node)
    {
        if (node.IsExpanded ? _collapsed.Remove(node.Id) : _collapsed.Add(node.Id))
            CollapsedGroupsChanged?.Invoke(CollapsedGroups);
    }
}
