using System.Text.Json;

namespace Invoklet.Services.Connections;

/// <summary>A folder of the user's connections; organization only, never written into an .inv file.</summary>
public sealed record ConnectionGroup(string Id, string Name, string? ParentId, int Order);

public sealed record ConnectionGroupingSnapshot(
    IReadOnlyList<ConnectionGroup> Groups,
    IReadOnlyDictionary<string, string> Assignments,
    string? Version);

/// <summary>
/// How the user organizes the reusable connections: nested groups with an explicit order, and the
/// group each connection (by name) is in. A connection is in exactly one place; one that is not
/// assigned, or whose group is gone, is in the fixed <see cref="UngroupedId"/> node.
/// </summary>
public sealed class ConnectionGroupingModel
{
    public const int MaxDepth = 3;
    public const string UngroupedId = "ungrouped";
    private readonly List<ConnectionGroup> _groups;
    private readonly Dictionary<string, string> _assignments;

    public ConnectionGroupingModel(ConnectionGroupingSnapshot? snapshot = null)
    {
        _groups = [];
        // A damaged or hand-edited file may hold duplicate IDs, unknown parents or a parent cycle;
        // such groups are kept, moved to the top level, rather than dropped with their connections.
        foreach (var group in snapshot?.Groups ?? [])
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || group.Id == UngroupedId
                || _groups.Any(g => g.Id == group.Id)) continue;
            _groups.Add(group with { Name = string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name.Trim() });
        }
        for (var i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            if (group.ParentId is { } parentId && (Find(parentId) is null || IsInSubtree(parentId, group.Id) || Depth(Find(parentId)) >= MaxDepth))
                _groups[i] = group with { ParentId = null };
        }
        _assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in snapshot?.Assignments ?? new Dictionary<string, string>())
            if (!string.IsNullOrEmpty(key) && value is not null) _assignments[key] = value;
        Normalize();
    }

    public IReadOnlyList<ConnectionGroup> Groups => _groups;
    public IReadOnlyDictionary<string, string> Assignments => _assignments;

    /// <summary>The groups directly under <paramref name="parentId"/> (null: the top level), in the user's order.</summary>
    public IReadOnlyList<ConnectionGroup> ChildrenOf(string? parentId)
        => [.. _groups.Where(g => g.ParentId == parentId).OrderBy(g => g.Order).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];

    public ConnectionGroup? Find(string id) => _groups.FirstOrDefault(g => g.Id == id);

    /// <summary>How deep the group is: 1 for a top-level group.</summary>
    public int DepthOf(string id) => Depth(Find(id));

    public ConnectionGroup Create(string name, string? parentId = null)
    {
        var parent = parentId is null ? null : Find(parentId) ?? throw new ArgumentException("Unknown parent group.", nameof(parentId));
        if (Depth(parent) >= MaxDepth) throw new InvalidOperationException(NestingLimitMessage);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name is required.", nameof(name));
        var group = new ConnectionGroup(Guid.NewGuid().ToString("N"), name.Trim(), parent?.Id, NextOrder(parent?.Id));
        _groups.Add(group);
        return group;
    }

    public void Rename(string id, string name)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name is required.", nameof(name));
        _groups[_groups.IndexOf(group)] = group with { Name = name.Trim() };
    }

    /// <summary>Puts the connection into a group; null or <see cref="UngroupedId"/> moves it to Ungrouped.</summary>
    public void MoveConnection(string connectionKey, string? groupId)
    {
        if (groupId is not null && groupId != UngroupedId && Find(groupId) is null) throw new ArgumentException("Unknown group.", nameof(groupId));
        _assignments[connectionKey] = groupId ?? UngroupedId;
    }

    /// <summary>Whether <see cref="MoveGroup"/> would accept the move.</summary>
    public bool CanMoveGroup(string id, string? newParentId)
    {
        if (Find(id) is null) return false;
        if (newParentId is null) return true;
        if (Find(newParentId) is null || IsInSubtree(newParentId, id)) return false;
        return Depth(Find(newParentId)) + SubtreeHeight(id) <= MaxDepth;
    }

    /// <summary>Nests a group (with everything in it) under another one, or at the top level for null; it goes last there.</summary>
    /// <exception cref="InvalidOperationException">The move would put a group inside itself or nest deeper than <see cref="MaxDepth"/>.</exception>
    public void MoveGroup(string id, string? newParentId)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        if (newParentId is not null && Find(newParentId) is null) throw new ArgumentException("Unknown parent group.", nameof(newParentId));
        if (newParentId is not null && IsInSubtree(newParentId, id)) throw new InvalidOperationException("A group cannot be moved into itself.");
        if (!CanMoveGroup(id, newParentId)) throw new InvalidOperationException(NestingLimitMessage);
        if (group.ParentId == newParentId) return;
        _groups[_groups.IndexOf(group)] = group with { ParentId = newParentId, Order = NextOrder(newParentId) };
    }

    /// <summary>Moves a group one place up (-1) or down (+1) among its siblings; false at either end.</summary>
    public bool Reorder(string id, int direction)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        var siblings = ChildrenOf(group.ParentId).ToList();
        var index = siblings.FindIndex(g => g.Id == id);
        var target = index + Math.Sign(direction);
        if (direction == 0 || target < 0 || target >= siblings.Count) return false;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            _groups[_groups.FindIndex(g => g.Id == siblings[i].Id)] = siblings[i] with { Order = i };
        return true;
    }

    /// <summary>Removes the group; its groups and connections move up to its parent (Ungrouped at the top level).</summary>
    public void Delete(string id)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        var parent = group.ParentId;
        var order = NextOrder(parent);
        foreach (var child in ChildrenOf(id))
            _groups[_groups.IndexOf(child)] = child with { ParentId = parent, Order = order++ };
        foreach (var key in _assignments.Where(p => p.Value == id).Select(p => p.Key).ToArray())
            _assignments[key] = parent ?? UngroupedId;
        _groups.Remove(group);
        Normalize();
    }

    /// <summary>Keeps a renamed connection in its group.</summary>
    public void RenameConnection(string oldKey, string newKey)
    {
        if (oldKey == newKey || !_assignments.Remove(oldKey, out var groupId)) return;
        _assignments[newKey] = groupId;
    }

    /// <summary>Forgets connections that are no longer in the list.</summary>
    public void Retain(IEnumerable<string> connectionKeys)
    {
        var keep = connectionKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in _assignments.Keys.Where(k => !keep.Contains(k)).ToArray()) _assignments.Remove(key);
    }

    public string GroupFor(string connectionKey) => _assignments.TryGetValue(connectionKey, out var id) && Find(id) is not null ? id : UngroupedId;

    public ConnectionGroupingSnapshot Snapshot(string? version = null) => new([.. _groups], new Dictionary<string, string>(_assignments), version);

    public static string NestingLimitMessage => $"Connection groups can be nested at most {MaxDepth} levels deep.";

    private int NextOrder(string? parentId) => _groups.Where(g => g.ParentId == parentId).Select(g => g.Order).DefaultIfEmpty(-1).Max() + 1;

    private int Depth(ConnectionGroup? group)
    {
        var depth = 0;
        for (var seen = new HashSet<string>(StringComparer.Ordinal); group is not null && seen.Add(group.Id); group = group.ParentId is { } p ? Find(p) : null)
            depth++;
        return depth;
    }

    // Levels from the group down to its deepest descendant: 1 for a group without subgroups.
    private int SubtreeHeight(string id, int guard = 0)
        => guard > MaxDepth * 4 ? 1 : 1 + _groups.Where(g => g.ParentId == id).Select(g => SubtreeHeight(g.Id, guard + 1)).DefaultIfEmpty(0).Max();

    // Whether candidateId is rootId or one of its descendants.
    private bool IsInSubtree(string candidateId, string rootId)
    {
        for (var (id, steps) = ((string?)candidateId, 0); id is not null && steps <= _groups.Count; id = Find(id)?.ParentId, steps++)
            if (id == rootId) return true;
        return false;
    }

    private void Normalize()
    {
        foreach (var key in _assignments.Keys.ToArray())
            if (_assignments[key] != UngroupedId && Find(_assignments[key]) is null)
                _assignments[key] = UngroupedId;
    }
}

/// <summary>
/// The groups in a file next to <c>connections.json</c>, so a Invoklet without groups reads the
/// connection list unchanged. Written to a temporary file and moved into place.
/// </summary>
public sealed class FileConnectionGroupingStore
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileConnectionGroupingStore(string connectionsPath) => _path = connectionsPath + ".groups.json";

    public string FilePath => _path;

    /// <exception cref="InvalidDataException">The file is damaged or from a newer Invoklet; it is left as it is.</exception>
    public ConnectionGroupingSnapshot Load()
    {
        if (!File.Exists(_path)) return new ConnectionGroupingModel().Snapshot();
        StoreFile? file;
        try { file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(_path)); }
        catch (JsonException ex) { throw new InvalidDataException($"The connection groups file is damaged: {_path}", ex); }
        if (file?.Format > FormatVersion)
            throw new InvalidDataException($"The connection groups were saved by a newer Invoklet: {_path}");
        file ??= new StoreFile();
        return new ConnectionGroupingSnapshot(
            [.. (file.Groups ?? []).Where(g => g is not null)],
            (file.Assignments ?? []).Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            file.Version);
    }

    public void Save(ConnectionGroupingSnapshot snapshot)
    {
        var file = new StoreFile
        {
            Format = FormatVersion,
            Version = snapshot.Version ?? DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Groups = [.. snapshot.Groups],
            Assignments = new(snapshot.Assignments, StringComparer.Ordinal),
        };
        // The folder is the connection list's, created when that list was first saved.
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class StoreFile
    {
        public int Format { get; set; }
        public string? Version { get; set; }
        public List<ConnectionGroup>? Groups { get; set; }
        public Dictionary<string, string>? Assignments { get; set; }
    }
}
