using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Services.Connections;
using Invoklet.Services.Secrets;
using Invoklet.Shared;
using ReactiveUI;

namespace Invoklet.Windows.Models;

// One name and text of a map parameter, such as an HTTP header.
public sealed class ConnectionMapRowViewModel : ReactiveObject
{
    internal event Action? Edited;

    public IReadOnlyList<string> SecretNames { get; }

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? "");
            this.RaisePropertyChanged(nameof(NameFieldLabel));
            this.RaisePropertyChanged(nameof(ValueFieldLabel));
            this.RaisePropertyChanged(nameof(InsertSecretLabel));
            this.RaisePropertyChanged(nameof(RemoveRowLabel));
            this.RaisePropertyChanged(nameof(NameError));
            this.RaisePropertyChanged(nameof(HasNameError));
            Edited?.Invoke();
        }
    } = "";

    public string Value
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? "");
            this.RaisePropertyChanged(nameof(NameError));
            this.RaisePropertyChanged(nameof(HasNameError));
            Edited?.Invoke();
        }
    } = "";

    // A value without a name would be dropped on save, so the row says what to do about it, right where it is.
    internal bool HasValueButNoName => Name.Trim().Length == 0 && Value.Trim().Length > 0;
    public bool HasNameError => HasValueButNoName;
    public string? NameError => HasValueButNoName ? "Give this row a name, or remove it." : null;

    // Picking a secret appends its placeholder to the value; the picker then shows nothing again.
    public string? SecretToInsert
    {
        get => null;
        set
        {
            if (value is null) return;
            Value += $"${{secret:{value}}}";
            this.RaisePropertyChanged();
        }
    }

    public bool HasSecrets => SecretNames.Count > 0;

    public string NameFieldLabel => $"{_fieldLabel} name: {Name}";
    public string ValueFieldLabel => $"Value for {_fieldLabel} {Name}";
    public string InsertSecretLabel => $"Insert secret into {_fieldLabel} {Name}";
    public string RemoveRowLabel => $"Remove {_fieldLabel} {Name}";
    private readonly string _fieldLabel;

    internal ConnectionMapRowViewModel(IReadOnlyList<string> secretNames, string fieldLabel)
    {
        SecretNames = secretNames;
        _fieldLabel = fieldLabel;
    }
}

// One parameter of the selected connection, edited the way its ConnectionParamType says.
public sealed class ConnectionFieldViewModel : ReactiveObject
{
    internal event Action? Edited;

    public ConnectionParam Param { get; }
    public IReadOnlyList<string> SecretNames { get; }

    public string Label => Param.Required ? Param.Label + " *" : Param.Label;
    public string AddRowLabel => $"Add {Param.Label.ToLowerInvariant()} row";
    public string? Description => Param.Description;
    public bool HasDescription => Param.Description is not null;
    public string? Example => Param.Example ?? Param.Default;
    public bool IsTextBox => Param.Type is ConnectionParamType.Text or ConnectionParamType.Number or ConnectionParamType.File;
    public bool CanBrowse => Param.Type is ConnectionParamType.File;
    public bool CanInsertSecret => Param.Type is ConnectionParamType.Text && SecretNames.Count > 0;
    public bool IsBoolean => Param.Type is ConnectionParamType.Boolean;
    public bool IsMap => Param.Type is ConnectionParamType.Map;
    public bool IsList => Param.Type is ConnectionParamType.List;

    // Text and Number; for List, one item per line.
    public string Text
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value ?? ""); Edited?.Invoke(); }
    } = "";

    public bool BooleanValue
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value); Edited?.Invoke(); }
    }

    public string? SecretToInsert
    {
        get => null;
        set
        {
            if (value is null) return;
            Text += $"${{secret:{value}}}";
            this.RaisePropertyChanged();
        }
    }

    public ObservableCollection<ConnectionMapRowViewModel> Rows { get; } = [];

    public ReactiveCommand<Unit, Unit> AddRowCommand { get; }
    public ReactiveCommand<ConnectionMapRowViewModel, Unit> RemoveRowCommand { get; }

    internal ConnectionFieldViewModel(ConnectionParam param, MdRunValue? value, IReadOnlyList<string> secretNames)
    {
        Param = param;
        SecretNames = secretNames;
        AddRowCommand = ReactiveCommand.Create(() => AddRow("", ""));
        RemoveRowCommand = ReactiveCommand.Create<ConnectionMapRowViewModel>(row =>
        {
            Rows.Remove(row);
            Edited?.Invoke();
        });
        switch (param.Type)
        {
            case ConnectionParamType.Boolean:
                BooleanValue = string.Equals(value?.Text ?? param.Default, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case ConnectionParamType.Map:
                foreach (var (name, text) in value?.Fields ?? []) AddRow(name, text.Text ?? "");
                break;
            case ConnectionParamType.List:
                Text = string.Join("\n", (value?.Items ?? []).Select(i => i.Text ?? ""));
                break;
            default:
                Text = value?.Text ?? "";
                break;
        }
    }

    private void AddRow(string name, string value)
    {
        var row = new ConnectionMapRowViewModel(SecretNames, Param.Label) { Name = name, Value = value };
        row.Edited += () => Edited?.Invoke();
        Rows.Add(row);
        Edited?.Invoke();
    }

    // A row with a value but no name blocks saving until it is named or removed (and counts as an unsaved change).
    // A row with neither is a new, empty row and is ignored.
    internal bool HasUnnamedValue => IsMap && Rows.Any(r => r.HasValueButNoName);

    internal string? MapError
    {
        get
        {
            if (!IsMap) return null;
            if (HasUnnamedValue)
                return $"{Param.Label}: a row has a value but no name. Give it a name or remove the row.";
            return Rows.Where(r => r.Name.Trim().Length > 0)
                .GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
                ? $"{Param.Label}: names must be unique (letter case aside)." : null;
        }
    }

    // Null for nothing set (and for a switch left at its default), so the parameter isn't written.
    internal MdRunValue? ToValue() => Param.Type switch
    {
        ConnectionParamType.Boolean => BooleanValue == string.Equals(Param.Default, "true", StringComparison.OrdinalIgnoreCase)
            ? null : MdRunValue.Of(BooleanValue ? "true" : "false"),
        ConnectionParamType.Map => Rows.Any(r => r.Name.Trim().Length > 0)
            ? MdRunValue.Map(Rows.Where(r => r.Name.Trim().Length > 0).GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => KeyValuePair.Create(g.First().Name.Trim(), MdRunValue.Of(g.First().Value))))
            : null,
        ConnectionParamType.List => Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList() is { Count: > 0 } items
            ? MdRunValue.List(items.Select(MdRunValue.Of))
            : null,
        _ => Text.Trim().Length > 0 ? MdRunValue.Of(Text.Trim()) : null,
    };
}

// One connection of the list: its name, its driver and a field per parameter the driver defines.
// Parameters the driver doesn't define (or all of them, for a driver this Invoklet lacks) are kept
// unchanged and written back.
public sealed class ConnectionItemViewModel : ReactiveObject
{
    private readonly Dictionary<string, MdRunValue> _otherParams;
    private readonly Dictionary<string, MdRunValue>? _extra;

    internal event Action? Edited;

    public IConnectionDriver? Driver { get; }
    public string DriverId { get; }
    public string DriverName => Driver?.DisplayName ?? $"{DriverId} (not available in this Invoklet)";
    // The status is part of the name, so a screen reader hears a test in progress or its outcome as it changes.
    public string AccessibleName => TestSummary is { } status ? $"{Name}, {DriverName}, {status}" : $"{Name}, {DriverName}";
    public ObservableCollection<ConnectionFieldViewModel> Fields { get; } = [];

    public string Name
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value ?? ""); this.RaisePropertyChanged(nameof(AccessibleName)); Edited?.Invoke(); }
    }

    // The name it has in the saved list (null until first saved), so a rename keeps its group.
    internal string? SavedName { get; set; }

    public string? KeptParamsNote => _otherParams.Count == 0 ? null
        : $"Also kept as written: {string.Join(", ", _otherParams.Keys)}.";

    public bool HasKeptParams => _otherParams.Count > 0;

    // The driver's view of the parameters; shown, but doesn't stop saving an unfinished connection.
    public string? Problems
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasProblems)); }
    }

    public bool HasProblems => Problems is not null;

    public string? TestResult
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(TestSummary));
            this.RaisePropertyChanged(nameof(IsTestRunning));
            this.RaisePropertyChanged(nameof(TestPassed));
            this.RaisePropertyChanged(nameof(TestFailed));
            this.RaisePropertyChanged(nameof(TestMessage));
            this.RaisePropertyChanged(nameof(AccessibleName));
        }
    }

    // One line for the connection list, so a test in progress or its outcome stays visible when another
    // connection is selected. The details pane shows the full TestResult.
    public bool IsTestRunning => TestResult == "Testing…";
    public bool TestPassed => TestResult?.StartsWith("Success: ", StringComparison.Ordinal) == true && !TestResult.Contains("\nFailed: ", StringComparison.Ordinal);
    public bool TestFailed => TestResult is not null && !IsTestRunning && !TestPassed;
    public string IconKey => Invoklet.UserControls.Models.ConnectionNavigationNode.DriverIconKey(DriverId);

    // The answer without its "Success: " label: a passed test shows an icon and this line.
    public string? TestMessage => TestResult?.StartsWith("Success: ", StringComparison.Ordinal) == true ? TestResult["Success: ".Length..] : TestResult;

    public string? TestSummary => TestResult switch
    {
        null => null,
        "Testing…" => "Testing…",
        _ when TestResult.StartsWith("Success: ", StringComparison.Ordinal) => "Test succeeded",
        _ => "Test failed",
    };

    // Counts edits, so a test that was running while the connection changed can tell its answer is
    // about parameters no longer shown.
    internal int EditCount { get; private set; }

    internal ConnectionItemViewModel(MdRunConnection connection, IConnectionDriver? driver, IReadOnlyList<string> secretNames)
    {
        Name = connection.Name;
        DriverId = connection.Driver;
        Driver = driver;
        _extra = connection.Extra;
        var schema = driver?.Params ?? [];
        _otherParams = connection.Params.Where(p => schema.All(s => s.Key != p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var param in schema.Where(p => !p.Advanced).Concat(schema.Where(p => p.Advanced)))
        {
            var field = new ConnectionFieldViewModel(param, connection.Params.GetValueOrDefault(param.Key), secretNames);
            field.Edited += OnChanged;
            Fields.Add(field);
        }
        UpdateProblems();
    }

    private void OnChanged()
    {
        EditCount++;
        TestResult = null;
        UpdateProblems();
        Edited?.Invoke();
    }

    private void UpdateProblems()
    {
        var connection = ToConnection();
        var placeholders = connection.Params.SelectMany(p => p.Value.Texts().Select(t => (p.Key, MdRunPlaceholders.Parse(t).Error)))
            .Where(p => p.Error is not null).Select(p => $"{p.Key}: {p.Error}");
        var problems = Fields.Select(f => f.MapError).OfType<string>().Concat(placeholders).Concat(Driver?.Validate(connection) ?? []).ToList();
        Problems = problems.Count == 0 ? null : string.Join("\n", problems);
    }

    // Driver parameters in the driver's order, then the ones it doesn't define.
    internal MdRunConnection ToConnection()
    {
        var parameters = new Dictionary<string, MdRunValue>(StringComparer.Ordinal);
        foreach (var param in Driver?.Params ?? [])
            if (Fields.First(f => f.Param.Key == param.Key).ToValue() is { } value) parameters[param.Key] = value;
        foreach (var (key, value) in _otherParams) parameters[key] = value;
        return new MdRunConnection { Name = Name.Trim(), Driver = DriverId, Params = parameters, Extra = _extra };
    }
}

public sealed class ConnectionResourceViewModel(string name, string kind, string details = "")
{
    // A column or a key says what it is in the tree itself; a table or a service is only a name there.
    public bool ShowsDetailsInTree => Kind is "Column" or "Foreign key" or "Method";
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string Details { get; } = details;
    public ObservableCollection<ConnectionResourceViewModel> Children { get; } = [];
}

public sealed record ConnectionResourceDetail(string Name, string Value);

// Tools → Connections: the user's own database, HTTP and gRPC connections, edited in one window.
// Edits stay in the window until OK writes the whole list; Cancel drops them. A list that can't be
// read is reported and never overwritten from here.
public sealed class ConnectionsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionStore _store;
    private string? _version;
    private readonly ConnectionDriverRegistry _drivers;
    private readonly ConnectionSchemas? _schemas;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly ISecretStore _secrets;
    private readonly FileConnectionGroupingStore _groupStore;
    // A damaged groups file is left as it is rather than replaced by an empty one on Save.
    private readonly bool _groupingUnreadable;
    public ConnectionGroupingModel Grouping { get; }
    private string _saved = "";
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _testCancellation;

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    public IReadOnlyList<IConnectionDriver> Drivers => _drivers.Drivers;

    public ConnectionItemViewModel? SelectedConnection
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(CanTest));
            RefreshResources();
        }
    }

    public bool HasSelection => SelectedConnection is not null;
    public bool IsEmpty => LoadError is null && Connections.Count == 0;
    // A connection with problems would only fail its test, or send the server half a configuration.
    public bool CanTest => SelectedConnection is { Driver: not null, HasProblems: false };

    public ObservableCollection<ConnectionResourceViewModel> Resources { get; } = [];
    public ObservableCollection<ConnectionResourceDetail> ResourceDetails { get; } = [];
    public ConnectionResourceViewModel? SelectedResource
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ShowDetails(value);
        }
    }

    public bool CanBrowse => _schemas is not null && SelectedConnection is { Driver: IConnectionSchemaDriver driver, HasProblems: false } item
        && (!driver.UsesScriptDirectory(item.ToConnection()) || HasBaseFolder);

    // A relative database path is read from a folder: the one of the script it belongs to, which this window doesn't know.
    public string BaseFolder
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? "");
            this.RaisePropertyChanged(nameof(HasBaseFolder));
            this.RaisePropertyChanged(nameof(CanBrowse));
            RefreshResources();
        }
    } = "";

    public bool HasBaseFolder => BaseFolder.Trim().Length > 0;

    public bool NeedsBaseFolder => SelectedConnection is { Driver: IConnectionSchemaDriver driver } item && driver.UsesScriptDirectory(item.ToConnection());

    private string? Folder => HasBaseFolder ? BaseFolder.Trim() : null;
    public bool IsBrowsing { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string BrowserStatus
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasBrowserStatus));
        }
    } = "Select a connection to browse its resources.";
    public bool HasBrowserStatus => !string.IsNullOrWhiteSpace(BrowserStatus);

    public string? LoadError { get; }
    public bool HasLoadError => LoadError is not null;

    public string? SaveError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(StoreError));
            this.RaisePropertyChanged(nameof(HasStoreError));
        }
    }

    public string? StoreError => LoadError ?? SaveError;
    public bool HasStoreError => StoreError is not null;

    // Names that are missing, invalid or clash: these block OK.
    public string? ValidationError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasValidationError));
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public bool HasValidationError => ValidationError is not null;
    public bool CanSave => LoadError is null && ValidationError is null;

    public bool IsTesting
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string StorePath => _store.FilePath;

    // Whether the connections differ from what was loaded or last saved; selection, test results
    // and other display state don't count.
    public bool HasChanges { get; private set; }

    public ReactiveCommand<IConnectionDriver, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseCommand { get; }

    public ConnectionsWindowViewModel(IConnectionStore store, ConnectionDriverRegistry drivers, ISecretStore secrets,
        ConnectionSchemas? schemas = null)
    {
        _schemas = schemas;
        _store = store;
        _groupStore = new FileConnectionGroupingStore(store.FilePath);
        try { Grouping = new ConnectionGroupingModel(_groupStore.Load()); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Grouping = new ConnectionGroupingModel();
            _groupingUnreadable = true;
        }
        Connections.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
        _drivers = drivers;
        _secrets = secrets;
        _secretNames = SecretNamesOf(secrets);
        try
        {
            var snapshot = store.LoadSnapshot();
            _version = snapshot.Version;
            foreach (var connection in snapshot.Connections) Add(connection).SavedName = connection.Name;
            _saved = Snapshot();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"{ex.Message} It is kept unchanged: {store.FilePath}";
        }
        SelectedConnection = Connections.FirstOrDefault();

        AddCommand = ReactiveCommand.Create<IConnectionDriver>(AddNew, this.WhenAnyValue(x => x.HasLoadError, error => !error));
        RemoveCommand = ReactiveCommand.Create(RemoveSelected, this.WhenAnyValue(x => x.HasSelection));
        TestCommand = ReactiveCommand.CreateFromTask(TestSelectedAsync,
            this.WhenAnyValue(x => x.CanTest, x => x.IsTesting, (valid, testing) => valid && !testing));
        BrowseCommand = ReactiveCommand.CreateFromTask(BrowseSelectedAsync,
            this.WhenAnyValue(x => x.CanBrowse, x => x.IsBrowsing, (valid, browsing) => valid && !browsing));
        Validate();
    }

    // Writes the list; false (nothing written) while a name is wrong or the list unreadable.
    public bool Save()
    {
        SaveError = null;
        Validate();
        if (!CanSave) return false;
        try
        {
            var connections = Connections.Select(c => c.ToConnection()).ToList();
            var groups = Connections.Select(c => c.SavedName is { } saved ? Grouping.GroupFor(saved) : ConnectionGroupingModel.UngroupedId).ToList();
            _version = _store.Save(connections, _version);
            for (var i = 0; i < Connections.Count; i++) Connections[i].SavedName = connections[i].Name;
            if (!_groupingUnreadable) SaveGrouping(connections, groups);
            _saved = JsonSerializer.Serialize(connections);
            UpdateHasChanges();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex is ConnectionStoreConflictException
                ? "The connection list changed in another window. Reopen it before saving. Your edits have not been written."
                : "Could not save the connections. Check access and available disk space, then try again. Your edits are still here.";
            return false;
        }
    }

    // Renamed connections keep their group and removed ones are forgotten. Assignments are rebuilt
    // from the groups read before saving, so swapping two names doesn't swap their groups. The list
    // itself is already saved, so a failure here only loses the organization change.
    private void SaveGrouping(IReadOnlyList<MdRunConnection> connections, IReadOnlyList<string> groups)
    {
        Grouping.Retain([]);
        for (var i = 0; i < connections.Count; i++) Grouping.MoveConnection(connections[i].Name, groups[i]);
        try { _groupStore.Save(Grouping.Snapshot()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static IReadOnlyList<string> SecretNamesOf(ISecretStore secrets)
    {
        try { return [.. secrets.LoadNames().Order(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }

    private ConnectionItemViewModel Add(MdRunConnection connection)
    {
        var item = new ConnectionItemViewModel(connection, _drivers.Find(connection.Driver), _secretNames);
        item.Edited += OnConnectionEdited;
        Connections.Add(item);
        return item;
    }

    private void AddNew(IConnectionDriver driver)
    {
        var name = driver.Id;
        for (var i = 2; Connections.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{driver.Id}{i}";
        SelectedConnection = Add(new MdRunConnection { Name = name, Driver = driver.Id });
        Validate();
    }

    private void RemoveSelected()
    {
        if (SelectedConnection is not { } item) return;
        var index = Connections.IndexOf(item);
        item.Edited -= OnConnectionEdited;
        Connections.RemoveAt(index);
        SelectedConnection = Connections.Count == 0 ? null : Connections[Math.Min(index, Connections.Count - 1)];
        Validate();
    }

    private async Task TestSelectedAsync()
    {
        if (SelectedConnection is not { Driver: { } driver, HasProblems: false } item) return;
        var previousTest = Interlocked.Exchange(ref _testCancellation, null);
        previousTest?.Cancel();
        var testCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _testCancellation, testCancellation);
        var token = testCancellation.Token;
        IsTesting = true;
        item.TestResult = "Testing…";
        // What is tested, and later read again, is the connection as it was when Test was pressed; an
        // edit meanwhile clears the result, and the old answer must not reappear over the new values.
        var connection = item.ToConnection();
        var edits = item.EditCount;
        bool Edited() => item.EditCount != edits;
        try
        {
            var secrets = SecretValues();
            var resolved = ConnectionPlaceholderResolver.Resolve(connection.Params,
                name => secrets.GetValueOrDefault(name), Environment.GetEnvironmentVariable);
            if (resolved.Errors.Count > 0)
            {
                item.TestResult = string.Join("\n", resolved.Errors);
                return;
            }
            var result = await driver.TestAsync(resolved.Params, token);
            if (token.IsCancellationRequested || Edited()) return;
            item.TestResult = (result.Succeeded ? "Success: " : "Failed: ") + result.Message;
            // A server that answers is read again, so scripts get its current classes; what it offers is shown in the browser, not in the answer.
            if (result.Succeeded && driver is IConnectionSchemaDriver && _schemas is not null)
            {
                await _schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.All, token);
                if (token.IsCancellationRequested || Edited()) return;
                if (_schemas.Cache.ErrorFor(connection) is { } error)
                    item.TestResult = "Success: " + result.Message + "\nFailed: its description couldn't be read: " + error;
                else
                {
                    item.TestResult = "Success: Connected.";
                    if (ReferenceEquals(SelectedConnection, item)) RefreshResources();
                }
            }
        }
        // A driver is free to throw for an unreachable or misconfigured server; that is the test's
        // answer, not a reason to take the window down.
        catch (OperationCanceledException ex)
        {
            if (!token.IsCancellationRequested && !Edited())
                item.TestResult = "Failed: " + (string.IsNullOrWhiteSpace(ex.Message) ? "The connection test was cancelled." : ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!Edited()) item.TestResult = "Failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, testCancellation))
            {
                Interlocked.CompareExchange(ref _testCancellation, null, testCancellation);
                IsTesting = false;
            }
            testCancellation.Dispose();
        }
    }

    private void RefreshResources()
    {
        _browseCancellation?.Cancel();
        _browseCancellation = null;
        IsBrowsing = false;
        Resources.Clear();
        ResourceDetails.Clear();
        SelectedResource = null;
        this.RaisePropertyChanged(nameof(CanBrowse));
        this.RaisePropertyChanged(nameof(NeedsBaseFolder));
        if (SelectedConnection is not { } item)
        {
            BrowserStatus = "Select a connection to browse its resources.";
            return;
        }
        if (item.Driver is not IConnectionSchemaDriver driver)
        {
            BrowserStatus = item.Driver is null
                ? "This connection's driver is unavailable."
                : "This connection type does not publish a browsable contract yet.";
            return;
        }
        if (item.HasProblems)
        {
            BrowserStatus = "Fix the connection settings before browsing its resources.";
            return;
        }
        if (driver.UsesScriptDirectory(item.ToConnection()) && !HasBaseFolder)
        {
            BrowserStatus = "This database path is relative to a script's folder: choose that folder below.";
            return;
        }
        if (_schemas is null)
        {
            BrowserStatus = "Resource browsing is unavailable because the schema service is not configured.";
            return;
        }
        var schema = _schemas.Cache.TryGetLoaded(item.ToConnection(), Folder);
        if (schema is null)
        {
            // A database is read from where it is, at no cost; a server is asked only when the user says so.
            if (driver.Category == ConnectionCategory.Database && CanBrowse)
            {
                BrowserStatus = "Loading resources…";
                _ = BrowseSelectedAsync();
                return;
            }
            BrowserStatus = "Not loaded yet. Use the reload button to read the contract.";
            return;
        }
        try
        {
            PopulateResources(item.DriverId, schema);
            BrowserStatus = schema.Summary;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            BrowserStatus = "The saved contract is damaged. Use the reload button to read it again.";
        }
    }

    private async Task BrowseSelectedAsync()
    {
        if (_schemas is null || SelectedConnection is not { Driver: IConnectionSchemaDriver driver, HasProblems: false } item)
            return;
        if (driver.UsesScriptDirectory(item.ToConnection()) && !HasBaseFolder)
        {
            BrowserStatus = "This database path is relative to a script's folder: choose that folder below.";
            return;
        }

        _browseCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _browseCancellation = cts;
        IsBrowsing = true;
        BrowserStatus = "Loading resources…";
        Resources.Clear();
        ResourceDetails.Clear();
        try
        {
            var secrets = SecretValues();
            var connection = item.ToConnection();
            var resolved = ConnectionPlaceholderResolver.Resolve(connection.Params,
                name => secrets.GetValueOrDefault(name), Environment.GetEnvironmentVariable);
            if (resolved.Errors.Count > 0)
            {
                BrowserStatus = "A secret or environment variable used by this connection is missing or invalid.";
                return;
            }
            await _schemas.EnsureAsync([connection], Folder, ConnectionSchemaRefresh.All, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedConnection, item)) return;
            var schema = _schemas.Cache.Get(connection, Folder);
            var error = _schemas.Cache.ErrorFor(connection, Folder);
            if (schema is null)
            {
                BrowserStatus = error ?? "The connection returned no resources.";
                return;
            }
            PopulateResources(item.DriverId, schema);
            BrowserStatus = schema.Summary;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            BrowserStatus = "Could not load resources. Check the connection settings and network access.";
        }
        finally
        {
            if (ReferenceEquals(_browseCancellation, cts))
            {
                _browseCancellation = null;
                IsBrowsing = false;
            }
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _testCancellation, null)?.Cancel();
        Interlocked.Exchange(ref _browseCancellation, null)?.Cancel();
    }

    private void PopulateResources(string driverId, ConnectionSchema schema)
    {
        Resources.Clear();
        ResourceDetails.Clear();
        SelectedResource = null;
        using var data = JsonDocument.Parse(schema.Data);
        if (driverId.Equals("sqlite", StringComparison.OrdinalIgnoreCase) && data.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var table in data.RootElement.EnumerateArray())
            {
                var name = ReadString(table, "Name") ?? "(unnamed table)";
                var isView = ReadBoolean(table, "IsView");
                var node = new ConnectionResourceViewModel(name, isView ? "View" : "Table",
                    isView ? "Database view" : "Database table");
                if (table.TryGetProperty("Columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var column in columns.EnumerateArray())
                    {
                        var columnName = ReadString(column, "Name") ?? "(unnamed column)";
                        var type = ReadString(column, "Type") ?? "";
                        var nullable = !ReadBoolean(column, "NotNull");
                        var primaryKey = ReadInt(column, "PrimaryKey") > 0;
                        node.Children.Add(new ConnectionResourceViewModel(columnName, "Column",
                            string.Join(" · ", new[] { type.Length == 0 ? "no type" : type, nullable ? "nullable" : "not null", primaryKey ? "primary key" : "" }.Where(s => s.Length > 0))));
                    }
                }
                if (table.TryGetProperty("ForeignKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                {
                    foreach (var key in keys.EnumerateArray())
                    {
                        var from = ReadStrings(key, "Columns");
                        var to = ReadStrings(key, "ReferencedColumns");
                        node.Children.Add(new ConnectionResourceViewModel(string.Join(", ", from), "Foreign key",
                            $"→ {ReadString(key, "Table")}" + (to.Count == 0 ? "" : $" ({string.Join(", ", to)})")));
                    }
                }
                Resources.Add(node);
            }
        }
        else if (driverId.Equals("grpc", StringComparison.OrdinalIgnoreCase) && data.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in data.RootElement.EnumerateArray())
            {
                var name = ReadString(service, "Service") ?? "(unnamed service)";
                var property = ReadString(service, "Property") ?? "";
                var clientType = ReadString(service, "ClientType") ?? "";
                var node = new ConnectionResourceViewModel(name, "gRPC service", "Client: Connections." + property);
                if (service.TryGetProperty("Methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var method in methods.EnumerateArray())
                    {
                        var kind = (ReadBoolean(method, "ClientStreaming"), ReadBoolean(method, "ServerStreaming")) switch
                        {
                            (false, false) => "unary",
                            (false, true) => "server streaming",
                            (true, false) => "client streaming",
                            _ => "bidirectional streaming",
                        };
                        node.Children.Add(new ConnectionResourceViewModel(ReadString(method, "Name") ?? "(unnamed method)", "Method",
                            $"{ShortName(ReadString(method, "Input"))} → {ShortName(ReadString(method, "Output"))} · {kind}"));
                    }
                }
                node.Children.Add(new ConnectionResourceViewModel(property, "Client property", clientType));
                Resources.Add(node);
            }
            foreach (var file in schema.Files)
                Resources.Add(new ConnectionResourceViewModel(file.Name, "Generated protobuf type", "Generated C# source"));
        }
        else
        {
            Resources.Add(new ConnectionResourceViewModel("Connection contract", "Resources", schema.Summary));
            foreach (var file in schema.Files)
                Resources.Add(new ConnectionResourceViewModel(file.Name, "Generated type", "Generated C# source"));
        }
        SelectedResource = Resources.FirstOrDefault();
    }

    private void ShowDetails(ConnectionResourceViewModel? resource)
    {
        ResourceDetails.Clear();
        if (resource is null) return;
        ResourceDetails.Add(new("Name", resource.Name));
        ResourceDetails.Add(new("Kind", resource.Kind));
        if (!string.IsNullOrWhiteSpace(resource.Details)) ResourceDetails.Add(new("Details", resource.Details));
        foreach (var child in resource.Children)
            ResourceDetails.Add(new(child.Kind, child.Name + (child.Details.Length == 0 ? "" : " — " + child.Details)));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string ShortName(string? fullName) => fullName is null ? "?" : fullName[(fullName.LastIndexOf('.') + 1)..];
    private static List<string> ReadStrings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [];
    private static bool ReadBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private Dictionary<string, string> SecretValues()
    {
        try { return _secrets.LoadSnapshot().Secrets.ToDictionary(s => s.Name, s => s.Value, StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }

    private string Snapshot() => JsonSerializer.Serialize(Connections.Select(c => c.ToConnection()));

    private void UpdateHasChanges()
    {
        // A value-only row isn't in the snapshot, but it is a change Save would refuse: the close question must come up.
        var changed = LoadError is null && (Snapshot() != _saved || Connections.SelectMany(c => c.Fields).Any(f => f.HasUnnamedValue));
        if (changed == HasChanges) return;
        HasChanges = changed;
        this.RaisePropertyChanged(nameof(HasChanges));
    }

    private void Validate()
    {
        UpdateHasChanges();
        this.RaisePropertyChanged(nameof(CanTest));
        var errors = MdRunConnection.Validate(Connections.Select(c => new MdRunConnection { Name = c.Name.Trim(), Driver = c.DriverId }));
        ValidationError = errors.FirstOrDefault() ?? Connections.SelectMany(c => c.Fields).Select(f => f.MapError).FirstOrDefault(e => e is not null);
    }

    private void OnConnectionEdited()
    {
        Validate();
        this.RaisePropertyChanged(nameof(CanBrowse));
        if (SelectedConnection is { } selected)
        {
            _browseCancellation?.Cancel();
            Resources.Clear();
            ResourceDetails.Clear();
            SelectedResource = null;
            BrowserStatus = selected.HasProblems
                ? "Fix the connection settings before browsing its resources."
                : "Settings changed. Use the reload button to read the contract again.";
        }
    }
}
