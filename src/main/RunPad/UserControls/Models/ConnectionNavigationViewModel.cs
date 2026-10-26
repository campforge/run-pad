using System.Collections.ObjectModel;
using ReactiveUI;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;

namespace RunPad.UserControls.Models;

public enum ConnectionNavigationNodeKind { Group, Connection }

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

    internal static ConnectionNavigationNode Connection(RpadConnection connection, IConnectionDriver? driver, int depth, string? problem) =>
        new(ConnectionNavigationNodeKind.Connection, connection.Name, connection.Name, depth)
        {
            DriverId = connection.Driver,
            DriverName = driver?.DisplayName ?? $"{connection.Driver} (not available in this RunPad)",
            IsDriverMissing = driver is null,
            IconKey = DriverIconKey(connection.Driver),
            Problem = problem,
        };

    public ConnectionNavigationNodeKind Kind { get; }

    /// <summary>The group's ID, or the connection's name (its key in the connection list).</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>0 for the top level of the Connections bundle.</summary>
    public int Depth { get; }

    public string? DriverId { get; private init; }
    public string? DriverName { get; private init; }
    public bool IsDriverMissing { get; private init; }

    /// <summary>Connection row: why it can't be used as it is (no driver, settings the driver rejects, an unreadable contract); null when none is known.</summary>
    public string? Problem { get; private init; }

    public bool HasProblem => Problem is not null;

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
            Name, DriverName, IsAttached ? "used by this script" : null, Problem is { } problem ? "problem: " + problem : null,
        }.Where(s => s is not null)),
        _ => $"{Name} group, {ConnectionCount} {(ConnectionCount == 1 ? "connection" : "connections")}"
             + (HasProblemInside ? ", has connections with problems" : ""),
    };

    public string ToolTip => IsConnection
        ? string.Join("\n", new[] { DriverName, IsAttached ? "Used by this script" : null, Problem }.Where(s => s is not null))
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
public sealed class ConnectionNavigationViewModel : ReactiveObject
{
    private readonly IConnectionStore _store;
    private readonly ConnectionDriverRegistry? _drivers;
    private readonly FileConnectionGroupingStore _groupStore;
    private readonly ISecretStore? _secrets;
    private readonly ConnectionSchemas? _schemas;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private ConnectionGroupingModel _grouping = new();
    private IReadOnlyList<RpadConnection> _connections = [];
    private IReadOnlyDictionary<string, string> _problems = new Dictionary<string, string>();
    private HashSet<string> _attached = new(StringComparer.Ordinal);
    private bool _groupingUnreadable;
    private int _refreshGeneration;

    public ConnectionNavigationViewModel(IConnectionStore store, ConnectionDriverRegistry? drivers = null,
        FileConnectionGroupingStore? groupStore = null, ISecretStore? secrets = null, ConnectionSchemas? schemas = null)
    {
        _store = store;
        _drivers = drivers;
        _secrets = secrets;
        _schemas = schemas;
        _groupStore = groupStore ?? new FileConnectionGroupingStore(store.FilePath);
        Nodes.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
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
            var problems = await Task.Run(() => FindProblems(connections)).ConfigureAwait(true);
            if (generation != _refreshGeneration) return;
            _connections = connections;
            _problems = problems;
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

    private (IReadOnlyList<RpadConnection>, ConnectionGroupingModel, string?, bool) Load()
    {
        IReadOnlyList<RpadConnection> connections;
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

    // What can be known without connecting: the driver, its own validation of the settings, and the
    // error of the last attempt to read the contract (gRPC). Each connection is checked on its own, so
    // one driver throwing doesn't hide the others.
    private Dictionary<string, string> FindProblems(IReadOnlyList<RpadConnection> connections)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (_drivers?.Find(connection.Driver) is not { } driver)
            {
                if (_drivers is not null) problems[connection.Name] = $"This RunPad has no '{connection.Driver}' driver.";
                continue;
            }
            try
            {
                if (driver.Validate(connection).FirstOrDefault() is { } invalid) problems[connection.Name] = invalid;
                else if (_schemas?.Cache.ErrorFor(connection) is { } schemaError) problems[connection.Name] = $"Its contract couldn't be read: {schemaError}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems[connection.Name] = $"Its settings couldn't be checked: {ex.Message}";
            }
        }
        return problems;
    }

    /// <summary>Marks the connections the active script uses (null: no script is active).</summary>
    public void SetAttached(IEnumerable<RpadConnection>? scriptConnections)
    {
        HasActiveScript = scriptConnections is not null;
        _attached = (scriptConnections ?? []).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var node in AllNodes().Where(n => n.IsConnection)) node.IsAttached = _attached.Contains(node.Id);
    }

    public bool CanEditGroups => IsLoaded && !_groupingUnreadable && Error is null;

    /// <summary>Adds a group at the top level or inside <paramref name="parent"/>; null when it is not allowed.</summary>
    public ConnectionNavigationNode? CreateGroup(ConnectionNavigationNode? parent, string name = "New Group")
    {
        if (!CanEditGroups) return null;
        var parentId = parent is { IsGroup: true } ? parent.Id : null;
        ConnectionGroup group;
        try { group = _grouping.Create(UniqueName(parentId, name), parentId); }
        catch (InvalidOperationException ex) { Notice = ex.Message; return null; }
        if (!Persist()) return null;
        if (parentId is not null) _collapsed.Remove(parentId);
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
        _collapsed.Remove(node.Id);
        return PersistAndRebuild();
    }

    public bool MoveConnection(ConnectionNavigationNode connection, string? groupId)
    {
        if (!CanEditGroups || !connection.IsConnection) return false;
        if (_grouping.GroupFor(connection.Id) == (groupId ?? ConnectionGroupingModel.UngroupedId)) return true;
        _grouping.MoveConnection(connection.Id, groupId);
        if (groupId is not null) _collapsed.Remove(groupId);
        return PersistAndRebuild();
    }

    public bool MoveGroup(ConnectionNavigationNode group, string? parentId)
    {
        if (!CanEditGroups || !group.IsGroup) return false;
        if (parentId == ConnectionGroupingModel.UngroupedId) parentId = null;
        try { _grouping.MoveGroup(group.Id, parentId); }
        catch (InvalidOperationException ex) { Notice = ex.Message; return false; }
        if (parentId is not null) _collapsed.Remove(parentId);
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

    /// <summary>Whether the connection can be offered to the active script: there is one, it doesn't use it yet, and this RunPad has its driver.</summary>
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
    public async Task<bool> AddToScriptAsync(RpadFrontMatter frontMatter, string? scriptPath, string connectionName,
        Func<string, Task<bool>> confirmPackages)
    {
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
            script.ConfirmPendingAdd();
        }
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

    private ConnectionNavigationNode ConnectionRow(RpadConnection connection, int depth)
    {
        var node = ConnectionNavigationNode.Connection(connection, _drivers?.Find(connection.Driver), depth, _problems.GetValueOrDefault(connection.Name));
        node.IsAttached = _attached.Contains(connection.Name);
        return node;
    }

    private void OnExpandedChanged(ConnectionNavigationNode node)
    {
        if (node.IsExpanded) _collapsed.Remove(node.Id);
        else _collapsed.Add(node.Id);
    }
}
