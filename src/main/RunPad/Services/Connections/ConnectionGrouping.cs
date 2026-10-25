using System.Text.Json;
using RunPad.Format.RPad;

namespace RunPad.Services.Connections;

/// <summary>Stable user-store organization; never serialized into an .rpad file.</summary>
public sealed record ConnectionGroup(string Id, string Name, string? ParentId, int Order);

public sealed record ConnectionGroupingSnapshot(
    IReadOnlyList<ConnectionGroup> Groups,
    IReadOnlyDictionary<string, string> Assignments,
    string? Version);

/// <summary>Bounded organization model for the unified navigation tree.</summary>
public sealed class ConnectionGroupingModel
{
    public const int MaxDepth = 3;
    public const string UngroupedId = "ungrouped";
    private readonly List<ConnectionGroup> _groups;
    private readonly Dictionary<string, string> _assignments;

    public ConnectionGroupingModel(ConnectionGroupingSnapshot? snapshot = null)
    {
        _groups = snapshot?.Groups.Where(g => g.Id != UngroupedId).ToList() ?? [];
        _assignments = snapshot?.Assignments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        Normalize();
    }

    public IReadOnlyList<ConnectionGroup> Groups => _groups;
    public IReadOnlyDictionary<string, string> Assignments => _assignments;

    public ConnectionGroup Create(string name, string? parentId = null)
    {
        var parent = parentId is null ? null : Find(parentId) ?? throw new ArgumentException("Unknown parent group.", nameof(parentId));
        if (Depth(parent) >= MaxDepth) throw new InvalidOperationException($"Connection groups may be nested only {MaxDepth} levels.");
        var group = new ConnectionGroup(Guid.NewGuid().ToString("N"), name.Trim(), parent?.Id, NextOrder(parent?.Id));
        if (group.Name.Length == 0) throw new ArgumentException("Group name is required.", nameof(name));
        _groups.Add(group);
        return group;
    }

    public void Rename(string id, string name)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name is required.", nameof(name));
        _groups[_groups.IndexOf(group)] = group with { Name = name.Trim() };
    }

    public void MoveConnection(string connectionKey, string? groupId)
    {
        if (groupId is not null && Find(groupId) is null) throw new ArgumentException("Unknown group.", nameof(groupId));
        _assignments[connectionKey] = groupId ?? UngroupedId;
    }

    public void Delete(string id)
    {
        var group = Find(id) ?? throw new ArgumentException("Unknown group.", nameof(id));
        var parent = group.ParentId;
        foreach (var child in _groups.Where(g => g.ParentId == id).ToArray())
            _groups[_groups.IndexOf(child)] = child with { ParentId = parent };
        foreach (var key in _assignments.Where(p => p.Value == id).Select(p => p.Key).ToArray())
            _assignments[key] = parent ?? UngroupedId;
        _groups.Remove(group);
        Normalize();
    }

    public string GroupFor(string connectionKey) => _assignments.TryGetValue(connectionKey, out var id) && Find(id) is not null ? id : UngroupedId;

    public ConnectionGroupingSnapshot Snapshot(string? version = null) => new([.. _groups], new Dictionary<string, string>(_assignments), version);

    private ConnectionGroup? Find(string id) => _groups.FirstOrDefault(g => g.Id == id);
    private int NextOrder(string? parentId) => _groups.Where(g => g.ParentId == parentId).Select(g => g.Order).DefaultIfEmpty(-1).Max() + 1;
    private int Depth(ConnectionGroup? group) => group is null ? 0 : 1 + (group.ParentId is null ? 0 : Depth(Find(group.ParentId)));
    private void Normalize()
    {
        var valid = _groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _assignments.Keys.ToArray())
            if (!_assignments.TryGetValue(key, out var id) || (id != UngroupedId && !valid.Contains(id)))
                _assignments[key] = UngroupedId;
    }
}

/// <summary>Sidecar persistence next to connections.json; legacy stores remain readable unchanged.</summary>
public sealed class FileConnectionGroupingStore
{
    private readonly string _path;
    public FileConnectionGroupingStore(string connectionsPath) => _path = connectionsPath + ".groups.json";
    public ConnectionGroupingSnapshot Load()
    {
        if (!File.Exists(_path)) return new ConnectionGroupingModel().Snapshot();
        var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(_path)) ?? new StoreFile();
        return new ConnectionGroupingSnapshot(file.Groups ?? [], file.Assignments ?? new Dictionary<string, string>(), file.Version);
    }
    public void Save(ConnectionGroupingSnapshot snapshot)
    {
        var file = new StoreFile { Version = DateTime.UtcNow.Ticks.ToString(), Groups = [.. snapshot.Groups], Assignments = new(snapshot.Assignments) };
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }
    private sealed class StoreFile { public string? Version { get; set; } public List<ConnectionGroup>? Groups { get; set; } public Dictionary<string, string>? Assignments { get; set; } }
}
