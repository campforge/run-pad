using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using ReactiveUI;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Shared;

namespace RunPad.Windows.Models;

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
    public bool IsTextBox => Param.Type is ConnectionParamType.Text or ConnectionParamType.Number;
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

    internal ConnectionFieldViewModel(ConnectionParam param, RpadValue? value, IReadOnlyList<string> secretNames)
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
    internal RpadValue? ToValue() => Param.Type switch
    {
        ConnectionParamType.Boolean => BooleanValue == string.Equals(Param.Default, "true", StringComparison.OrdinalIgnoreCase)
            ? null : RpadValue.Of(BooleanValue ? "true" : "false"),
        ConnectionParamType.Map => Rows.Any(r => r.Name.Trim().Length > 0)
            ? RpadValue.Map(Rows.Where(r => r.Name.Trim().Length > 0).GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => KeyValuePair.Create(g.First().Name.Trim(), RpadValue.Of(g.First().Value))))
            : null,
        ConnectionParamType.List => Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList() is { Count: > 0 } items
            ? RpadValue.List(items.Select(RpadValue.Of))
            : null,
        _ => Text.Trim().Length > 0 ? RpadValue.Of(Text.Trim()) : null,
    };
}

// One connection of the list: its name, its driver and a field per parameter the driver defines.
// Parameters the driver doesn't define (or all of them, for a driver this RunPad lacks) are kept
// unchanged and written back.
public sealed class ConnectionItemViewModel : ReactiveObject
{
    private readonly Dictionary<string, RpadValue> _otherParams;
    private readonly Dictionary<string, RpadValue>? _extra;

    internal event Action? Edited;

    public IConnectionDriver? Driver { get; }
    public string DriverId { get; }
    public string DriverName => Driver?.DisplayName ?? $"{DriverId} (not available in this RunPad)";
    // The status is part of the name, so a screen reader hears a test in progress or its outcome as it changes.
    public string AccessibleName => TestSummary is { } status ? $"{Name}, {DriverName}, {status}" : $"{Name}, {DriverName}";
    public ObservableCollection<ConnectionFieldViewModel> Fields { get; } = [];

    public string Name
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value ?? ""); this.RaisePropertyChanged(nameof(AccessibleName)); Edited?.Invoke(); }
    }

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
            this.RaisePropertyChanged(nameof(AccessibleName));
        }
    }

    // One line for the connection list, so a test in progress or its outcome stays visible when another
    // connection is selected. The details pane shows the full TestResult.
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

    internal ConnectionItemViewModel(RpadConnection connection, IConnectionDriver? driver, IReadOnlyList<string> secretNames)
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
        var placeholders = connection.Params.SelectMany(p => p.Value.Texts().Select(t => (p.Key, RpadPlaceholders.Parse(t).Error)))
            .Where(p => p.Error is not null).Select(p => $"{p.Key}: {p.Error}");
        var problems = Fields.Select(f => f.MapError).OfType<string>().Concat(placeholders).Concat(Driver?.Validate(connection) ?? []).ToList();
        Problems = problems.Count == 0 ? null : string.Join("\n", problems);
    }

    // Driver parameters in the driver's order, then the ones it doesn't define.
    internal RpadConnection ToConnection()
    {
        var parameters = new Dictionary<string, RpadValue>(StringComparer.Ordinal);
        foreach (var param in Driver?.Params ?? [])
            if (Fields.First(f => f.Param.Key == param.Key).ToValue() is { } value) parameters[param.Key] = value;
        foreach (var (key, value) in _otherParams) parameters[key] = value;
        return new RpadConnection { Name = Name.Trim(), Driver = DriverId, Params = parameters, Extra = _extra };
    }
}

// Tools → Connections: the user's own database, HTTP and gRPC connections, edited in one window.
// Edits stay in the window until OK writes the whole list; Cancel drops them. A list that can't be
// read is reported and never overwritten from here.
public sealed class ConnectionsWindowViewModel : ViewModelBase
{
    private readonly IConnectionStore _store;
    private string? _version;
    private readonly ConnectionDriverRegistry _drivers;
    private readonly ConnectionSchemas? _schemas;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly ISecretStore _secrets;
    private string _saved = "";

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
        }
    }

    public bool HasSelection => SelectedConnection is not null;
    public bool IsEmpty => LoadError is null && Connections.Count == 0;
    // A connection with problems would only fail its test, or send the server half a configuration.
    public bool CanTest => SelectedConnection is { Driver: not null, HasProblems: false };

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

    public ConnectionsWindowViewModel(IConnectionStore store, ConnectionDriverRegistry drivers, ISecretStore secrets,
        ConnectionSchemas? schemas = null)
    {
        _schemas = schemas;
        _store = store;
        Connections.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
        _drivers = drivers;
        _secrets = secrets;
        _secretNames = SecretNamesOf(secrets);
        try
        {
            var snapshot = store.LoadSnapshot();
            _version = snapshot.Version;
            foreach (var connection in snapshot.Connections) Add(connection);
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
            _version = _store.Save(connections, _version);
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

    private static IReadOnlyList<string> SecretNamesOf(ISecretStore secrets)
    {
        try { return [.. secrets.LoadSnapshot().Secrets.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }

    private ConnectionItemViewModel Add(RpadConnection connection)
    {
        var item = new ConnectionItemViewModel(connection, _drivers.Find(connection.Driver), _secretNames);
        item.Edited += Validate;
        Connections.Add(item);
        return item;
    }

    private void AddNew(IConnectionDriver driver)
    {
        var name = driver.Id;
        for (var i = 2; Connections.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{driver.Id}{i}";
        SelectedConnection = Add(new RpadConnection { Name = name, Driver = driver.Id });
        Validate();
    }

    private void RemoveSelected()
    {
        if (SelectedConnection is not { } item) return;
        var index = Connections.IndexOf(item);
        item.Edited -= Validate;
        Connections.RemoveAt(index);
        SelectedConnection = Connections.Count == 0 ? null : Connections[Math.Min(index, Connections.Count - 1)];
        Validate();
    }

    private async Task TestSelectedAsync()
    {
        if (SelectedConnection is not { Driver: { } driver, HasProblems: false } item) return;
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
            var result = await driver.TestAsync(resolved.Params, CancellationToken.None);
            if (Edited()) return;
            item.TestResult = (result.Succeeded ? "Success: " : "Failed: ") + result.Message;
            // A server that answers is read again, so scripts get its current classes.
            if (result.Succeeded && driver is IConnectionSchemaDriver && _schemas is not null)
            {
                item.TestResult += "\nReading its description…";
                await _schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.All, CancellationToken.None);
                if (Edited()) return;
                item.TestResult = "Success: " + result.Message + "\n" + (_schemas.Cache.ErrorFor(connection) is { } error
                    ? "Failed: its description couldn't be read: " + error
                    : "Scripts get its classes on their next build.");
            }
        }
        // A driver is free to throw for an unreachable or misconfigured server; that is the test's
        // answer, not a reason to take the window down.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!Edited()) item.TestResult = "Failed: " + ex.Message;
        }
        finally { IsTesting = false; }
    }

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
        var errors = RpadConnection.Validate(Connections.Select(c => new RpadConnection { Name = c.Name.Trim(), Driver = c.DriverId }));
        ValidationError = errors.FirstOrDefault() ?? Connections.SelectMany(c => c.Fields).Select(f => f.MapError).FirstOrDefault(e => e is not null);
    }
}
