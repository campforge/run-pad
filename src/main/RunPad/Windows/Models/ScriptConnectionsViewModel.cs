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

    public string DriverName
    {
        get;
        internal set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(AccessibleName)); }
    } = "";

    public string AccessibleName => $"{Name}, {DriverName}";

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
// copies it into the script (its secrets stay placeholders) together with the NuGet packages its
// driver needs, which become ordinary packages of the script; the editor knows its client class right
// away once the change is saved. Import keeps a script's connection in the user's own list.
public sealed class ScriptConnectionsViewModel : ReactiveObject
{
    private readonly IConnectionStore? _store;
    private readonly ISecretStore? _secrets;
    private readonly ConnectionDriverRegistry _drivers;
    private IReadOnlyList<RpadConnection> _original = [];
    private List<RpadConnection> _mine = [];
    private string? _version;
    private List<RpadConnection>? _pendingImport;
    private string? _pendingVersion;
    // The script's packages as drafted in the window, and how to add one there.
    private Func<IReadOnlySet<string>> _scriptPackages = () => new HashSet<string>();
    private Action<string, string> _addPackage = (_, _) => { };

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
        set { if (ReferenceEquals(field, value)) return; this.RaiseAndSetIfChanged(ref field, value); CancelImportConfirmation(); }
    }

    public bool IsCSharp
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public bool HasItems => Items.Count > 0;
    public string AvailableHint => _mine.Count == 0
        ? "No saved connections. Create one in Tools → Connections."
        : Available.Count == 0 ? "All saved connections are already in this script." : "Choose a connection";

    public bool IsImportConfirmationPending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ImportMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ConfirmImportCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelImportCommand { get; }

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
        ConfirmImportCommand = ReactiveCommand.Create(ConfirmImport, this.WhenAnyValue(x => x.IsImportConfirmationPending));
        CancelImportCommand = ReactiveCommand.Create(CancelImportConfirmation, this.WhenAnyValue(x => x.IsImportConfirmationPending));
    }

    // The Dependencies window's own package draft, which connections add their packages to.
    internal void UsePackages(Func<IReadOnlySet<string>> scriptPackages, Action<string, string> addPackage)
    {
        _scriptPackages = scriptPackages;
        _addPackage = addPackage;
    }

    // After the script's packages change: a missing package is a problem of the connection.
    internal void OnPackagesChanged() => Refresh();

    internal void Load(RpadFrontMatter? frontMatter)
    {
        SelectedItem = null;
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
            var snapshot = _store.LoadSnapshot();
            _version = snapshot.Version;
            _mine = [.. snapshot.Connections];
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
        if (_drivers.Find(connection.Driver) is { } driver)
            foreach (var (id, version) in driver.Packages)
                if (!_scriptPackages().Contains(id)) _addPackage(id, version);
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
        var candidate = _mine.ToList();
        var index = candidate.FindIndex(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) candidate[index] = Clone(item.Connection);
        else candidate.Add(Clone(item.Connection));
        if (index >= 0 && Json([_mine[index]]) != Json([item.Connection]))
        {
            _pendingImport = candidate;
            _pendingVersion = _version;
            ImportMessage = $"Replace your saved connection '{item.Name}'? This is saved immediately; Cancel in Dependencies will not undo it.";
            IsImportConfirmationPending = true;
            return;
        }
        CommitImport(candidate, _version);
    }

    private void ConfirmImport()
    {
        if (_pendingImport is not { } candidate) return;
        var version = _pendingVersion;
        CancelImportConfirmation();
        CommitImport(candidate, version);
    }

    private void CancelImportConfirmation()
    {
        _pendingImport = null;
        _pendingVersion = null;
        IsImportConfirmationPending = false;
        ImportMessage = null;
    }

    private void CommitImport(List<RpadConnection> candidate, string? version)
    {
        if (_store is null) return;
        try
        {
            _version = _store.Save(candidate, version);
            _mine = candidate;
            ImportMessage = "Saved to your connections. Cancel in Dependencies will not undo this import.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = ex is ConnectionStoreConflictException
                ? "Your connections changed in another window. Nothing was imported; reopen Dependencies and try again."
                : "Your connections could not be saved. Check access and available disk space, then try again.";
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
            else
            {
                problems.AddRange(driver.Validate(connection));
                problems.AddRange(GeneratedConnections.MissingPackages(driver, _scriptPackages())
                    .Select(id => $"Needs the NuGet package {id}: add it on the Packages tab."));
            }
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
        this.RaisePropertyChanged(nameof(AvailableHint));
    }

    private HashSet<string>? SecretNames()
    {
        if (_secrets is null) return null;
        try { return new HashSet<string>(_secrets.LoadSnapshot().Secrets.Select(s => s.Name), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Json(IReadOnlyList<RpadConnection> connections) => JsonSerializer.Serialize(connections);

    private static RpadConnection Clone(RpadConnection connection) =>
        JsonSerializer.Deserialize<RpadConnection>(JsonSerializer.Serialize(connection))!;
}
