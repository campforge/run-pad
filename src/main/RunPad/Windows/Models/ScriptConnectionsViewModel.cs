using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;

namespace RunPad.Windows.Models;

// One of the script's connections, with what RunPad can tell about it.
public sealed class ScriptConnectionItemViewModel : ReactiveObject
{
    internal ScriptConnectionItemViewModel(RpadConnection connection) => Connection = connection;

    internal RpadConnection Connection { get; }

    public string Name => Connection.Name;

    // How the script's code reaches it.
    public string CodeName => "Connections." + RpadConnection.CodeName(Connection.Name);

    public string DriverName { get; internal set; } = "";

    // Problems that would fail the build: a missing driver, invalid parameters, a missing secret.
    public string? Problem
    {
        get;
        internal set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasProblem)); }
    }

    public bool HasProblem => Problem is not null;

    // How it relates to the user's own connections: kept there, different there, or not there.
    public string Status
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";
}

// Dependencies → Connections: the script's connections: list. Adding one of the user's connections
// copies it into the script (its secrets stay placeholders), and the editor knows its client class
// right away once the change is saved. Import keeps a script's connection in the user's own list.
public sealed class ScriptConnectionsViewModel : ReactiveObject
{
    private readonly IConnectionStore? _store;
    private readonly ISecretStore? _secrets;
    private readonly ConnectionDriverRegistry _drivers;
    private IReadOnlyList<RpadConnection> _original = [];
    private List<RpadConnection> _mine = [];

    internal event Action? Edited;

    public ObservableCollection<ScriptConnectionItemViewModel> Items { get; } = [];

    // The user's connections the script doesn't list yet.
    public ObservableCollection<RpadConnection> Available { get; } = [];

    public RpadConnection? SelectedAvailable
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ScriptConnectionItemViewModel? SelectedItem
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsCSharp
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public bool HasItems => Items.Count > 0;

    // Why the user's list couldn't be read or written; Import is off while it can't be read.
    public string? StoreError
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasStoreError)); }
    }

    public bool HasStoreError => StoreError is not null;

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    public ScriptConnectionsViewModel(IConnectionStore? store, ISecretStore? secrets, ConnectionDriverRegistry? drivers)
    {
        _store = store;
        _secrets = secrets;
        _drivers = drivers ?? new ConnectionDriverRegistry();
        Items.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasItems));
        AddCommand = ReactiveCommand.Create(AddSelected, this.WhenAnyValue(x => x.SelectedAvailable, (RpadConnection? c) => c is not null));
        RemoveCommand = ReactiveCommand.Create(RemoveSelected, this.WhenAnyValue(x => x.SelectedItem, (ScriptConnectionItemViewModel? i) => i is not null));
        ImportCommand = ReactiveCommand.Create(ImportSelected,
            this.WhenAnyValue(x => x.SelectedItem, x => x.HasStoreError, (ScriptConnectionItemViewModel? i, bool error) => i is not null && !error && _store is not null));
    }

    internal void Load(RpadFrontMatter? frontMatter)
    {
        _original = frontMatter?.Connections ?? [];
        IsCSharp = frontMatter?.Language is null or RpadLanguage.CSharp;
        LoadMine();
        Items.Clear();
        foreach (var connection in _original) Items.Add(new ScriptConnectionItemViewModel(Clone(connection)));
        Refresh();
    }

    internal List<RpadConnection> ToList() => [.. Items.Select(i => i.Connection)];

    internal bool HasChanges => Json(_original) != Json(ToList());

    private void LoadMine()
    {
        if (_store is null) return;
        try
        {
            _mine = [.. _store.Load()];
            StoreError = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _mine = [];
            StoreError = $"Your connections can't be read: {ex.Message}";
        }
    }

    private void AddSelected()
    {
        if (SelectedAvailable is not { } connection) return;
        var item = new ScriptConnectionItemViewModel(Clone(connection));
        Items.Add(item);
        SelectedItem = item;
        Refresh();
        Edited?.Invoke();
    }

    private void RemoveSelected()
    {
        if (SelectedItem is not { } item) return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Refresh();
        Edited?.Invoke();
    }

    // Writes the script's connection into the user's list at once (replacing one of the same name);
    // the script itself doesn't change.
    private void ImportSelected()
    {
        if (SelectedItem is not { } item || _store is null) return;
        LoadMine();
        if (HasStoreError) return;
        var index = _mine.FindIndex(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _mine[index] = Clone(item.Connection);
        else _mine.Add(Clone(item.Connection));
        try
        {
            _store.Save(_mine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = $"Your connections couldn't be saved: {ex.Message}";
        }
        Refresh();
    }

    private void Refresh()
    {
        var secretNames = SecretNames();
        foreach (var item in Items)
        {
            var connection = item.Connection;
            var driver = _drivers.Find(connection.Driver);
            item.DriverName = driver?.DisplayName ?? connection.Driver;
            var problems = new List<string>();
            if (!IsCSharp) problems.Add("Connections can be used in C# scripts only, for now.");
            if (driver is null) problems.Add($"This RunPad has no '{connection.Driver}' driver.");
            else problems.AddRange(driver.Validate(connection));
            foreach (var text in connection.Params.Values.SelectMany(v => v.Texts()))
            {
                var parsed = RpadPlaceholders.Parse(text);
                if (parsed.Error is { } error) problems.Add(error);
                problems.AddRange(parsed.Parts
                    .Where(p => p.Kind == RpadPlaceholders.Kind.Secret && secretNames is not null && !secretNames.Contains(p.Value))
                    .Select(p => $"The Secret Store has no secret named '{p.Value}'."));
            }
            item.Problem = problems.Count == 0 ? null : string.Join("\n", problems.Distinct());

            var mine = _mine.FirstOrDefault(c => string.Equals(c.Name, connection.Name, StringComparison.OrdinalIgnoreCase));
            item.Status = _store is null ? ""
                : mine is null ? "Not in your connections: Import keeps it there."
                : Json([mine]) == Json([connection]) ? "Same as in your connections."
                : "Differs from your connection of the same name: Import replaces yours.";
        }
        Available.Clear();
        foreach (var connection in _mine.Where(m => Items.All(i => !string.Equals(i.Name, m.Name, StringComparison.OrdinalIgnoreCase))))
            Available.Add(connection);
        SelectedAvailable = Available.FirstOrDefault();
    }

    private HashSet<string>? SecretNames()
    {
        if (_secrets is null) return null;
        try { return new HashSet<string>(_secrets.Load().Select(s => s.Name), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Json(IReadOnlyList<RpadConnection> connections) => JsonSerializer.Serialize(connections);

    private static RpadConnection Clone(RpadConnection connection) =>
        JsonSerializer.Deserialize<RpadConnection>(JsonSerializer.Serialize(connection))!;
}
