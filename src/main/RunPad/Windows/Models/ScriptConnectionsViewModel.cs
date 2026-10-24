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
    private readonly ConnectionSchemas? _connectionSchemas;
    private ConnectionSchemaCache? _schemas => _connectionSchemas?.Cache;
    // The script's folder, which a database file's relative path is read from.
    private string? _scriptDirectory;
    private IReadOnlyList<RpadConnection> _original = [];
    private List<RpadConnection> _mine = [];
    private string? _version;
    private List<RpadConnection>? _pendingImport;
    private string? _pendingVersion;
    private RpadConnection? _pendingAdd;
    private int _addConfirmationVersion;
    // The script's packages as drafted in the window, and how to add or remove one there.
    private Func<IReadOnlySet<string>> _scriptPackages = () => new HashSet<string>();
    private Action<string, string> _addPackage = (_, _) => { };
    private Action<string> _removePackage = _ => { };
    // Packages the draft gained for a connection added since Load, so removing that connection
    // again before saving takes away what adding it brought and nothing the script had.
    private readonly Dictionary<ScriptConnectionItemViewModel, List<string>> _addedPackages = [];

    internal event Action? Edited;

    // The add confirmation is shown by DependenciesWindow as a modal dialog, rather than inline
    // beneath the connection picker. Tests and alternate hosts can still drive the commands directly.
    internal Func<string, Task<bool>>? AddConfirmationDialog { get; set; }

    public ObservableCollection<ScriptConnectionItemViewModel> Items { get; } = [];

    // The user's connections the script doesn't list yet.
    public ObservableCollection<RpadConnection> Available { get; } = [];

    public RpadConnection? SelectedAvailable
    {
        get;
        set { if (ReferenceEquals(field, value)) return; this.RaiseAndSetIfChanged(ref field, value); CancelAddConfirmation(); }
    }

    public ScriptConnectionItemViewModel? SelectedItem
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CanRefreshSelected));
            CancelImportConfirmation();
        }
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

    // Set while Add is waiting for the user to accept the NuGet packages the connection's driver
    // needs; nothing in the script changes until ConfirmAddCommand runs.
    public bool IsAddConfirmationPending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? AddMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ConfirmAddCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddCommand { get; }

    // The window that edits the user's connection list (Tools → Connections): given the id of a driver
    // to start a new connection of it, or null to just open the list; completes with true when the
    // user saved the list. Null when there is none, which turns the commands below off.
    internal Func<string?, Task<bool>>? ConnectionsEditor
    {
        get;
        set { field = value; this.RaisePropertyChanged(nameof(CanEditConnections)); }
    }

    // One New Connection entry per driver this RunPad has.
    public IReadOnlyList<IConnectionDriver> Drivers => _drivers.Drivers;

    public bool CanEditConnections => ConnectionsEditor is not null && _store is not null && !HasReadError;

    // Why a connection wasn't added, or what creating one led to; no confirmation is waiting.
    public string? CreateMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // Starts a new connection of the chosen driver in the Connections window. A connection the user
    // saves there is chosen in the list and offered to the script through the same confirmation as any
    // other Add; the script's draft is not touched until that is confirmed.
    public ReactiveCommand<IConnectionDriver, Unit> NewCommand { get; }

    // Opens the Connections window on the user's list, to edit or create several.
    public ReactiveCommand<Unit, Unit> ManageCommand { get; }

    // Why the user's list couldn't be read; Import is off until it can be.
    public string? ReadError
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); OnStoreErrorChanged(); }
    }

    public bool HasReadError => ReadError is not null;

    // Why the last import couldn't be written; Import stays on to try again.
    public string? WriteError
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); OnStoreErrorChanged(); }
    }

    public string? StoreError => ReadError ?? WriteError;
    public bool HasStoreError => StoreError is not null;

    private void OnStoreErrorChanged()
    {
        this.RaisePropertyChanged(nameof(HasReadError));
        this.RaisePropertyChanged(nameof(CanEditConnections));
        this.RaisePropertyChanged(nameof(StoreError));
        this.RaisePropertyChanged(nameof(HasStoreError));
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    // Reads the selected connection's description (a gRPC server's services) again: a server whose
    // contract changed is otherwise read only when its description is missing or failed.
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public bool CanRefreshSelected => SelectedItem is { } item && _connectionSchemas is not null
        && _drivers.Find(item.Connection.Driver) is IConnectionSchemaDriver;

    public bool IsRefreshing
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? RefreshMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ScriptConnectionsViewModel(IConnectionStore? store, ISecretStore? secrets, ConnectionDriverRegistry? drivers,
        ConnectionSchemas? schemas = null)
    {
        _connectionSchemas = schemas;
        _store = store;
        _secrets = secrets;
        _drivers = drivers ?? new ConnectionDriverRegistry();
        Items.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasItems));
        AddCommand = ReactiveCommand.Create(RequestAdd, this.WhenAnyValue(x => x.SelectedAvailable, (RpadConnection? c) => c is not null));
        RemoveCommand = ReactiveCommand.Create(RemoveSelected, this.WhenAnyValue(x => x.SelectedItem, (ScriptConnectionItemViewModel? i) => i is not null));
        ImportCommand = ReactiveCommand.Create(ImportSelected,
            this.WhenAnyValue(x => x.SelectedItem, x => x.HasReadError, (ScriptConnectionItemViewModel? i, bool error) => i is not null && !error && _store is not null));
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshSelectedAsync, this.WhenAnyValue(
            x => x.SelectedItem, x => x.IsRefreshing, (ScriptConnectionItemViewModel? _, bool refreshing) => CanRefreshSelected && !refreshing));
        ConfirmImportCommand = ReactiveCommand.Create(ConfirmImport, this.WhenAnyValue(x => x.IsImportConfirmationPending));
        CancelImportCommand = ReactiveCommand.Create(CancelImportConfirmation, this.WhenAnyValue(x => x.IsImportConfirmationPending));
        var canEdit = this.WhenAnyValue(x => x.CanEditConnections, x => x.HasReadError, (can, error) => can && !error);
        NewCommand = ReactiveCommand.CreateFromTask<IConnectionDriver>(driver => EditConnectionsAsync(driver.Id), canEdit);
        ManageCommand = ReactiveCommand.CreateFromTask(() => EditConnectionsAsync(null), canEdit);
        ConfirmAddCommand = ReactiveCommand.Create(ConfirmAdd, this.WhenAnyValue(x => x.IsAddConfirmationPending));
        CancelAddCommand = ReactiveCommand.Create(CancelAddConfirmation, this.WhenAnyValue(x => x.IsAddConfirmationPending));
    }

    // The Dependencies window's own package draft, which connections add their packages to.
    internal void UsePackages(Func<IReadOnlySet<string>> scriptPackages, Action<string, string> addPackage,
        Action<string>? removePackage = null)
    {
        _scriptPackages = scriptPackages;
        _addPackage = addPackage;
        _removePackage = removePackage ?? (_ => { });
    }

    // After the script's packages change: a missing package is a problem of the connection.
    internal void OnPackagesChanged() => Refresh();

    internal void SetScriptPath(string? path)
    {
        _scriptDirectory = path is null ? null : Path.GetDirectoryName(path);
        Refresh();
    }

    internal void Load(RpadFrontMatter? frontMatter)
    {
        SelectedItem = null;
        CancelAddConfirmation();
        RefreshMessage = null;
        _addedPackages.Clear();
        WriteError = null;
        _original = frontMatter?.Connections ?? [];
        IsCSharp = frontMatter?.Language is null or RpadLanguage.CSharp;
        LoadMine();
        ReadSecretNames();
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
            ReadError = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _mine = [];
            ReadError = $"Your connections can't be read: {ex.Message}";
        }
    }

    // Packages already in the script's draft (in any version) are left alone, so adding never
    // overwrites a version the user set; only packages the driver would newly bring are asked about.
    private void RequestAdd()
    {
        if (SelectedAvailable is not { } connection) return;
        CreateMessage = null;
        var driver = _drivers.Find(connection.Driver);
        if (driver is null)
        {
            // Nothing is added: the script would list a connection no build could use.
            CreateMessage = $"'{connection.Name}' can't be added: it uses the '{connection.Driver}' driver, which this RunPad doesn't have. "
                + "Use a RunPad version that has it, or choose another connection.";
            return;
        }
        var schemas = _schemas;
        if (driver is IConnectionSchemaDriver && schemas is not null && schemas.Get(connection, _scriptDirectory) is null
            && schemas.ErrorFor(connection, _scriptDirectory) is { } schemaError)
        {
            CreateMessage = $"'{connection.Name}' can't be added because its contract couldn't be read: {schemaError}";
            return;
        }
        var needed = driver.Packages.Where(p => !_scriptPackages().Contains(p.Key)).ToList();
        if (needed.Count == 0)
        {
            ApplyAdd(connection);
            return;
        }
        _pendingAdd = connection;
        AddMessage = $"Add '{connection.Name}' ({driver.DisplayName}) to this script? Its client is built into the script, "
            + $"which also gets the NuGet package{(needed.Count == 1 ? "" : "s")} the connection needs: "
            + string.Join(", ", needed.Select(p => $"{p.Key} {p.Value}")) + ". Cancel leaves the script as it is.";
        if (AddConfirmationDialog is { } show)
        {
            _ = ConfirmAddFromDialogAsync(show, AddMessage, _addConfirmationVersion);
            return;
        }
        IsAddConfirmationPending = true;
    }

    internal void CancelPendingAdd() => CancelAddConfirmation();

    private async Task ConfirmAddFromDialogAsync(Func<string, Task<bool>> show, string message, int version)
    {
        try
        {
            var confirmed = await show(message).ConfigureAwait(true);
            if (version != _addConfirmationVersion) return;
            if (confirmed) ConfirmAdd();
            else CancelAddConfirmation();
        }
        catch
        {
            if (version == _addConfirmationVersion)
            {
                CancelAddConfirmation();
                CreateMessage = "The connection-add confirmation could not be shown.";
            }
        }
    }

    private async Task EditConnectionsAsync(string? newDriverId)
    {
        if (ConnectionsEditor is not { } editor) return;
        CreateMessage = null;
        // Both would be about a list that is about to change.
        CancelAddConfirmation();
        CancelImportConfirmation();
        var before = _mine.ToList();
        bool saved;
        try
        {
            saved = await editor(newDriverId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CreateMessage = $"The Connections window couldn't be opened: {ex.Message}";
            return;
        }
        // Closed without saving: the list and the script are as they were.
        if (!saved) return;
        LoadMine();
        Refresh();
        if (HasReadError) return;
        // Managing the list only reloads it; the script changes only for a connection created here.
        if (newDriverId is null) return;
        var created = FindCreated(before, _mine);
        if (created is null) return;
        if (Available.FirstOrDefault(c => string.Equals(c.Name, created.Name, StringComparison.OrdinalIgnoreCase)) is not { } offered)
        {
            CreateMessage = $"Saved '{created.Name}'. This script already has a connection called {RpadConnection.CodeName(created.Name)} in code, so it was not added.";
            return;
        }
        SelectedAvailable = offered;
        RequestAdd();
    }

    private void ConfirmAdd()
    {
        if (_pendingAdd is not { } connection) return;
        CancelAddConfirmation();
        ApplyAdd(connection);
    }

    private void CancelAddConfirmation()
    {
        _addConfirmationVersion++;
        _pendingAdd = null;
        IsAddConfirmationPending = false;
        AddMessage = null;
    }

    private void ApplyAdd(RpadConnection connection)
    {
        var schemas = _schemas;
        if (_drivers.Find(connection.Driver) is IConnectionSchemaDriver && schemas is not null
            && schemas.Get(connection, _scriptDirectory) is null
            && schemas.ErrorFor(connection, _scriptDirectory) is { } schemaError)
        {
            CreateMessage = $"'{connection.Name}' can't be added because its contract couldn't be read: {schemaError}";
            return;
        }
        var item = new ScriptConnectionItemViewModel(Clone(connection));
        Items.Add(item);
        SelectedItem = item;
        if (_drivers.Find(connection.Driver) is { } driver)
        {
            var added = new List<string>();
            foreach (var (id, version) in driver.Packages)
            {
                if (_scriptPackages().Contains(id)) continue;
                _addPackage(id, version);
                if (_scriptPackages().Contains(id)) added.Add(id);
            }
            if (added.Count > 0) _addedPackages[item] = added;
        }
        Refresh();
        Edited?.Invoke();
    }

    private void RemoveSelected()
    {
        if (SelectedItem is not { } item) return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        if (_addedPackages.Remove(item, out var added))
            foreach (var id in added)
            {
                // A package another connection still needs stays, and goes with that one instead.
                var needing = Items.FirstOrDefault(i => _drivers.Find(i.Connection.Driver)?.Packages.Keys
                    .Contains(id, StringComparer.OrdinalIgnoreCase) == true);
                if (needing is null) _removePackage(id);
                else if (_addedPackages.TryGetValue(needing, out var theirs)) theirs.Add(id);
                else _addedPackages[needing] = [id];
            }
        Refresh();
        Edited?.Invoke();
    }

    private async Task RefreshSelectedAsync()
    {
        if (SelectedItem is not { } item || _connectionSchemas is not { } schemas) return;
        var connection = item.Connection;
        IsRefreshing = true;
        RefreshMessage = $"Reading the description of '{item.Name}'…";
        try
        {
            await schemas.EnsureAsync([connection], _scriptDirectory, ConnectionSchemaRefresh.All, CancellationToken.None);
            RefreshMessage = schemas.Cache.ErrorFor(connection, _scriptDirectory) is { } error
                ? $"The description of '{item.Name}' couldn't be read: {error}"
                : $"'{item.Name}' read again: {schemas.Cache.Get(connection, _scriptDirectory)?.Summary}";
        }
        finally
        {
            IsRefreshing = false;
            Refresh();
        }
    }

    // Writes the script's connection into the user's list at once (replacing one of the same name);
    // the script itself doesn't change.
    private void ImportSelected()
    {
        if (SelectedItem is not { } item || _store is null) return;
        WriteError = null;
        LoadMine();
        if (HasReadError) return;
        var candidate = _mine.ToList();
        // A saved connection with the same name in code is replaced too: keeping both would make
        // any script that lists them together fail to build.
        var match = FindMine(item.Name);
        var index = match is null ? -1 : candidate.IndexOf(match);
        if (index >= 0) candidate[index] = Clone(item.Connection);
        else candidate.Add(Clone(item.Connection));
        if (index >= 0 && Json([_mine[index]]) != Json([item.Connection]))
        {
            var mine = _mine[index].Name;
            _pendingImport = candidate;
            _pendingVersion = _version;
            ImportMessage = (string.Equals(mine, item.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"Replace your saved connection '{mine}'?"
                    : $"Replace your saved connection '{mine}' with '{item.Name}'? Both are {RpadConnection.CodeName(item.Name)} in code.")
                + " This is saved immediately; Cancel in Dependencies will not undo it.";
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
            WriteError = ex is ConnectionStoreConflictException
                ? "Your connections changed in another window. Nothing was imported; reopen Dependencies and try again."
                : "Your connections could not be saved. Check access and available disk space, then try again.";
        }
        Refresh();
    }

    // The saved connection of that name, or else the one with the same name in code.
    private RpadConnection? FindMine(string name) =>
        _mine.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? _mine.FirstOrDefault(c => SameCodeName(c.Name, name));

    // As RpadConnection.Validate compares them: two such connections can't be in one script.
    private static bool SameCodeName(string a, string b) =>
        string.Equals(RpadConnection.CodeName(a), RpadConnection.CodeName(b), StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        var secretNames = SecretNames();
        // Validate reports each connection's problems as it reaches it, so the messages that
        // appear when a connection joins the list are that connection's.
        var listed = new List<RpadConnection>();
        var reported = 0;
        foreach (var item in Items)
        {
            var connection = item.Connection;
            listed.Add(connection);
            var listErrors = RpadConnection.Validate(listed);
            var problems = listErrors.Skip(reported).ToList();
            reported = listErrors.Count;
            var driver = _drivers.Find(connection.Driver);
            item.DriverName = driver?.DisplayName ?? connection.Driver;
            if (!IsCSharp) problems.Add("Connections can be used in C# scripts only, for now.");
            if (driver is null)
            {
                // Without a driver id, Validate has already said so.
                if (!string.IsNullOrWhiteSpace(connection.Driver)) problems.Add($"This RunPad has no '{connection.Driver}' driver.");
            }
            else
            {
                problems.AddRange(driver.Validate(connection));
                problems.AddRange(GeneratedConnections.MissingPackages(driver, _scriptPackages())
                    .Select(id => $"Needs the NuGet package {id}: add it on the Packages tab."));
                if (driver is IConnectionSchemaDriver && _schemas?.Get(connection, _scriptDirectory) is null
                    && _schemas?.ErrorFor(connection, _scriptDirectory) is { } why)
                    problems.Add($"Its description couldn't be read: {why}");
            }
            // A malformed placeholder is one of Validate's messages.
            foreach (var text in connection.Params.Values.SelectMany(v => v.Texts()))
            {
                var parsed = RpadPlaceholders.Parse(text);
                problems.AddRange(parsed.Parts
                    .Where(p => p.Kind == RpadPlaceholders.Kind.Secret && secretNames is not null && !secretNames.Contains(p.Value))
                    .Select(p => $"The Secret Store has no secret named '{p.Value}'."));
            }
            item.Problem = problems.Count == 0 ? null : string.Join("\n", problems.Distinct());

            var mine = FindMine(connection.Name);
            item.Status = _store is null ? ""
                : mine is null ? "Not in your connections: Import keeps it there."
                : !string.Equals(mine.Name, connection.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"Has the same name in code as your connection '{mine.Name}': Import replaces yours."
                : Json([mine]) == Json([connection]) ? "Same as in your connections."
                : "Differs from your connection of the same name: Import replaces yours.";
        }
        // Clearing the list lets the bound picker clear the selection, so remember it first.
        var previous = SelectedAvailable?.Name;
        Available.Clear();
        foreach (var connection in _mine.Where(m => Items.All(i => !SameCodeName(i.Name, m.Name))))
            Available.Add(connection);
        SelectedAvailable = Available.FirstOrDefault(c => string.Equals(c.Name, previous, StringComparison.OrdinalIgnoreCase))
            ?? Available.FirstOrDefault();
        this.RaisePropertyChanged(nameof(AvailableHint));
    }

    // Reading the store decrypts every value (DPAPI on Windows) to learn the names, so it is read once
    // per Load rather than on each refresh, which every package edit triggers.
    private HashSet<string>? _secretNames;
    private bool _secretNamesRead;

    private HashSet<string>? SecretNames()
    {
        if (!_secretNamesRead) ReadSecretNames();
        return _secretNames;
    }

    private void ReadSecretNames()
    {
        _secretNamesRead = true;
        _secretNames = null;
        if (_secrets is null) return;
        try { _secretNames = new HashSet<string>(_secrets.LoadSnapshot().Secrets.Select(s => s.Name), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
    }

    // The window may also rename or delete connections, so "a name that wasn't there" isn't enough: a
    // renamed connection has a new name too. One that only changed its name keeps its driver and
    // parameters, so it is matched to a connection that disappeared with the same content; what is
    // left over (the last one, if several) was created.
    internal static RpadConnection? FindCreated(IReadOnlyList<RpadConnection> before, IReadOnlyList<RpadConnection> after)
    {
        var known = before.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = after.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vanished = before.Where(c => !current.Contains(c.Name)).Select(Content).ToList();
        RpadConnection? created = null;
        foreach (var connection in after.Where(c => !known.Contains(c.Name)))
        {
            var index = vanished.IndexOf(Content(connection));
            if (index >= 0) vanished.RemoveAt(index); // renamed
            else created = connection;
        }
        return created;
    }

    private static string Content(RpadConnection connection)
    {
        var copy = Clone(connection);
        copy.Name = "";
        return JsonSerializer.Serialize(copy);
    }

    private static string Json(IReadOnlyList<RpadConnection> connections) => JsonSerializer.Serialize(connections);

    private static RpadConnection Clone(RpadConnection connection) =>
        JsonSerializer.Deserialize<RpadConnection>(JsonSerializer.Serialize(connection))!;
}
