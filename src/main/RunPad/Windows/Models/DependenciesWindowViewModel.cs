using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Shared;

namespace RunPad.Windows.Models;

// ── Package list row ───────────────────────────────────────────────────────────

// One row of either package list (online results or the local column). Its Add/Update/Remove
// buttons bind to the window VM's AddRowCommand/UpdateRowCommand/RemoveRowCommand with the row as
// the parameter — not commands per row: a local cache can hold hundreds of packages, and creating
// three ReactiveCommands per row on every list rebuild stalled the UI. Version is what the row offers: the latest version from the search
// result, the locally cached one, or — for the script's own packages — the script's version.
// One line of a package's dependency list: a target framework (header) or a package below it.
public sealed record PackageDetailLine(string Text, bool IsHeader);

public sealed class PackageListItemViewModel : ReactiveObject
{
    private string? _scriptVersion;
    private bool _isEditable;

    public string Id { get; }
    public string Version { get; }
    public string Description { get; }
    public long TotalDownloads { get; }
    public bool HasDescription => Description.Length > 0;

    // The version this package has in the dialog's draft, or null when it isn't in the script.
    public string? ScriptVersion
    {
        get => _scriptVersion;
        set
        {
            this.RaiseAndSetIfChanged(ref _scriptVersion, value);
            RaiseStateChanged();
        }
    }

    // False while no script is open: rows still browse, but offer no actions.
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditable, value);
            RaiseStateChanged();
        }
    }

    public bool IsInScript => ScriptVersion is not null;
    public bool CanAdd => IsEditable && !IsInScript;
    public bool CanUpdate => IsEditable && IsInScript && Version.Length > 0
                             && !string.Equals(ScriptVersion, Version, StringComparison.OrdinalIgnoreCase);
    public bool CanRemove => IsEditable && IsInScript;

    public string InScriptLabel => ScriptVersion is null || ScriptVersion == Version
        ? "In script"
        : $"In script: {ScriptVersion}";

    public string UpdateLabel => $"Use {Version} in the script (currently {ScriptVersion})";

    internal PackageListItemViewModel(string id, string version, string description, long totalDownloads,
        string? scriptVersion, bool isEditable)
    {
        Id = id;
        Version = version;
        Description = description;
        TotalDownloads = totalDownloads;
        _scriptVersion = scriptVersion;
        _isEditable = isEditable;
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsInScript));
        this.RaisePropertyChanged(nameof(CanAdd));
        this.RaisePropertyChanged(nameof(CanUpdate));
        this.RaisePropertyChanged(nameof(CanRemove));
        this.RaisePropertyChanged(nameof(InScriptLabel));
        this.RaisePropertyChanged(nameof(UpdateLabel));
    }

    internal static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B",
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F0}K",
        _ => n.ToString("N0")
    };
}

// ── Package source selector item ───────────────────────────────────────────────

// An entry of the source ComboBox under the online list: "All sources", one enabled source, or the
// "(configure sources...)" command item that opens NuGetSourcesWindow (handled by the view).
public sealed class PackageSourceOption
{
    public string Label { get; }
    public string? SourceName { get; }
    public bool IsConfigure { get; }

    private PackageSourceOption(string label, string? sourceName, bool isConfigure)
    {
        Label = label;
        SourceName = sourceName;
        IsConfigure = isConfigure;
    }

    public static PackageSourceOption AllSources { get; } = new("All sources", null, false);
    public static PackageSourceOption Configure { get; } = new("(configure sources...)", null, true);
    internal static PackageSourceOption ForSource(string name) => new(name, name, false);

    public override string ToString() => Label;
}

// ── References row ─────────────────────────────────────────────────────────────

public sealed class DependencyEntryViewModel
{
    public string Value { get; }
    public string RemoveLabel => $"Remove {Value}";
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    internal DependencyEntryViewModel(string value, Action<DependencyEntryViewModel> remove)
    {
        Value = value;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }
}

// ── Main ViewModel ─────────────────────────────────────────────────────────────

// Edits a draft copy of the script's packages/references/namespaces. The RpadFrontMatter passed to
// SetFrontMatter is never touched until ApplyChanges (the window's OK button); Cancel/Escape/closing
// the window simply drops the draft. MainWindowViewModel.DependenciesWindowClosed then lets the
// editor tab notice a real metadata change, mark itself dirty, save and refresh IntelliSense.
//
// Packages tab, modeled on LINQPad's NuGet Manager: a local column (the script's packages, then the
// local package cache, with its own filter), an online column (search in the selected source, paged)
// and a details column (Include Prerelease, versions, Add/Update/Remove). Selecting a row in either
// list shows it in the details column.
public class DependenciesWindowViewModel : ViewModelBase, IDisposable
{
    // Debounce before firing a NuGet search after the user stops typing — avoids a request (and a
    // list rebuild) per keystroke. 500ms: short pauses mid-word don't count as
    // "finished typing"; Enter still searches immediately (SearchNow).
    private static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(500);

    private sealed class DraftPackage(string id, string version)
    {
        public string Id { get; } = id;
        public string Version { get; set; } = version;
    }

    private readonly IPackageSearchService _search;
    private readonly ILocalPackageCacheService _cache;
    private readonly INuGetSourceStore _sources;
    private readonly TimeSpan _searchDebounce;
    private readonly CompositeDisposable _disposables = new();

    private RpadFrontMatter? _original;
    private readonly List<DraftPackage> _draftPackages = [];
    private bool _applied;
    private bool _disposed;

    // Online results, the query/page they belong to; the local cache is read once (Retry re-reads).
    private IReadOnlyList<PackageSearchResult> _onlineResults = [];
    private string? _onlineQuery;
    private int _skip;
    private bool _hasNextPage;
    private IReadOnlyList<CachedPackageInfo>? _cachedPackages;
    private bool _cacheLoadAttempted;

    // Server versions of the package in the details column, before the prerelease filter.
    private IReadOnlyList<string>? _serverVersions;

    // Every request carries the generation it started in; a late answer from an older generation
    // (even one from a service that ignores its CancellationToken) is dropped.
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private CancellationTokenSource? _versionsCts;
    private int _versionsGeneration;
    private CancellationTokenSource? _detailsCts;
    private int _detailsGeneration;
    // What the sources said about a package version, so going back to one needs no new request;
    // null is also an answer (no source supplies it). Dropped when the sources change.
    private readonly Dictionary<string, PackageDetails?> _detailsCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectingPackage;

    // While a bound collection is rebuilt the view pushes a null selection back; ignore it and
    // re-assert the VM's own selection afterwards.
    private bool _rebuildingOnline;
    private bool _rebuildingLocal;
    private bool _rebuildingVersions;
    private bool _rebuildingSources;

    private string _searchText = "";
    private string _localFilter = "";
    private bool _includePrerelease;
    private PackageSourceOption _selectedSourceOption = PackageSourceOption.AllSources;
    private string? _sourcesError;
    private bool _isSearching;
    private string? _searchError;
    private string? _onlineMessage;
    private string? _localMessage;
    private string? _localError;
    private PackageListItemViewModel? _selectedOnlinePackage;
    private PackageListItemViewModel? _selectedLocalPackage;
    private PackageListItemViewModel? _selectedPackage;
    private string? _detailsId;
    private string? _selectedScriptVersion;
    private string? _selectedVersion;
    private bool _isLoadingVersions;
    private string? _versionsError;
    private bool _hasChanges;
    private string _newReference = "";
    private string? _referenceError;
    private string _namespacesText = "";
    private string? _namespaceError;
    private DependencyEntryViewModel? _selectedReference;

    // ── Script / draft ─────────────────────────────────────────────────────────

    public bool HasFrontMatter => _original is not null;

    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasChanges, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    // The Save button: something changed and nothing blocks applying it.
    public bool CanApply => HasChanges && !HasNamespaceError && !Arguments.HasError;

    // ── Online column ──────────────────────────────────────────────────────────

    // Replaced as a whole on every rebuild (one change notification), never edited item by item.
    public IReadOnlyList<PackageListItemViewModel> OnlinePackages { get; private set; } = [];
    public bool HasOnlinePackages => OnlinePackages.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= "";
            if (_searchText == value) return;
            this.RaiseAndSetIfChanged(ref _searchText, value);
            this.RaisePropertyChanged(nameof(HasSearchText));
            TriggerSearch(immediate: false);
        }
    }

    public bool HasSearchText => SearchText.Length > 0;

    public bool IncludePrerelease
    {
        get => _includePrerelease;
        set
        {
            if (_includePrerelease == value) return;
            this.RaiseAndSetIfChanged(ref _includePrerelease, value);
            RefreshVersions();
            TriggerSearch(immediate: true);
        }
    }

    public ObservableCollection<PackageSourceOption> SourceOptions { get; } = [];

    // The configure item is never selected here — the view opens the configurator instead and puts
    // the previous source back.
    public PackageSourceOption SelectedSourceOption
    {
        get => _selectedSourceOption;
        set
        {
            if (_rebuildingSources || value is null || value.IsConfigure || ReferenceEquals(value, _selectedSourceOption))
                return;
            this.RaiseAndSetIfChanged(ref _selectedSourceOption, value);
            TriggerSearch(immediate: true);
        }
    }

    // Failure to read RunPad's NuGet.Config; search then has no named sources to offer.
    public string? SourcesError
    {
        get => _sourcesError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _sourcesError, value);
            this.RaisePropertyChanged(nameof(HasSourcesError));
        }
    }

    public bool HasSourcesError => SourcesError is not null;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSearching, value);
            this.RaisePropertyChanged(nameof(ShowSearchProgress));
            this.RaisePropertyChanged(nameof(ShowOnlineLoader));
        }
    }

    public bool ShowSearchProgress => IsSearching;

    // The loader in place of the online list: a search runs and there is nothing (current) to show —
    // a first search, or a page change, which drops the old page right away.
    public bool ShowOnlineLoader => IsSearching && OnlinePackages.Count == 0;

    // Failure of the online search, with Retry.
    public string? SearchError
    {
        get => _searchError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _searchError, value);
            this.RaisePropertyChanged(nameof(HasSearchError));
        }
    }

    public bool HasSearchError => SearchError is not null;

    // Informational state of the online list (idle / searching / nothing found); never an error.
    public string? OnlineMessage
    {
        get => _onlineMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _onlineMessage, value);
            this.RaisePropertyChanged(nameof(ShowOnlineMessage));
        }
    }

    public bool ShowOnlineMessage => OnlineMessage is not null;

    public bool HasPreviousPage => _skip > 0 && _onlineQuery is not null;
    public bool HasNextPage => _hasNextPage && _onlineQuery is not null;

    public PackageListItemViewModel? SelectedOnlinePackage
    {
        get => _selectedOnlinePackage;
        set
        {
            if (_rebuildingOnline) return;
            this.RaiseAndSetIfChanged(ref _selectedOnlinePackage, value);
            if (value is null) return;
            ClearLocalSelection();
            SetSelectedPackage(value);
        }
    }

    // ── Local column ───────────────────────────────────────────────────────────

    // Replaced as a whole on every rebuild: Clear + one Add per cached package raised hundreds of
    // collection notifications, each re-laid-out by the ListBox.
    public IReadOnlyList<PackageListItemViewModel> LocalPackages { get; private set; } = [];
    public bool HasLocalPackages => LocalPackages.Count > 0;

    public string LocalFilter
    {
        get => _localFilter;
        set
        {
            value ??= "";
            if (_localFilter == value) return;
            this.RaiseAndSetIfChanged(ref _localFilter, value);
            RebuildLocal();
        }
    }

    public string? LocalMessage
    {
        get => _localMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _localMessage, value);
            this.RaisePropertyChanged(nameof(ShowLocalMessage));
        }
    }

    public bool ShowLocalMessage => LocalMessage is not null;

    // Failure to read the local package cache, with Retry; the script's own packages still show.
    public string? LocalError
    {
        get => _localError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _localError, value);
            this.RaisePropertyChanged(nameof(HasLocalError));
        }
    }

    public bool HasLocalError => LocalError is not null;

    public PackageListItemViewModel? SelectedLocalPackage
    {
        get => _selectedLocalPackage;
        set
        {
            if (_rebuildingLocal) return;
            this.RaiseAndSetIfChanged(ref _selectedLocalPackage, value);
            if (value is null) return;
            ClearOnlineSelection();
            SetSelectedPackage(value);
        }
    }

    // ── Package details ────────────────────────────────────────────────────────

    // The row shown in the details column — the last one selected in either list.
    public PackageListItemViewModel? SelectedPackage => _selectedPackage;

    public bool HasSelection => SelectedPackage is not null;
    public string DetailsTitle => SelectedPackage?.Id ?? "";

    public string DetailsDescription => SelectedPackage is { HasDescription: true } p
        ? p.Description
        : "No description available.";

    public string DetailsDownloads => SelectedPackage is { TotalDownloads: > 0 } p
        ? $"{PackageListItemViewModel.FormatDownloads(p.TotalDownloads)} downloads"
        : "";

    public bool HasDetailsDownloads => DetailsDownloads.Length > 0;

    public string? SelectedScriptVersion
    {
        get => _selectedScriptVersion;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedScriptVersion, value);
            this.RaisePropertyChanged(nameof(IsSelectedInScript));
            this.RaisePropertyChanged(nameof(PackageStatusText));
        }
    }

    public bool IsSelectedInScript => SelectedScriptVersion is not null;

    public string PackageStatusText => SelectedScriptVersion is { } v
        ? $"In this script: {v}"
        : "Not in this script";

    public ObservableCollection<string> AvailableVersions { get; } = [];

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (_rebuildingVersions) return;
            var changed = !string.Equals(_selectedVersion, value, StringComparison.Ordinal);
            this.RaiseAndSetIfChanged(ref _selectedVersion, value);
            // Its dependencies differ from version to version.
            if (changed && !_selectingPackage) LoadDetails();
        }
    }

    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingVersions, value);
    }

    public string? VersionsError
    {
        get => _versionsError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _versionsError, value);
            this.RaisePropertyChanged(nameof(HasVersionsError));
        }
    }

    public bool HasVersionsError => VersionsError is not null;

    // ── Package metadata: dependencies, license, project page ─────────────────

    // Opens a web address of the card in the browser; set by the window. Only http(s) addresses the
    // card itself showed are opened.
    internal Func<Uri, Task>? LinkLauncher { get; set; }

    public bool IsLoadingDetails
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? DetailsError
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasDetailsError)); }
    }

    public bool HasDetailsError => DetailsError is not null;

    // What the sources supplied for the selected package version; null while loading or when they supply nothing.
    private PackageDetails? Details
    {
        get;
        set
        {
            field = value;
            this.RaisePropertyChanged(nameof(DetailsLicense));
            this.RaisePropertyChanged(nameof(HasDetailsLicense));
            this.RaisePropertyChanged(nameof(DetailsLicenseUrl));
            this.RaisePropertyChanged(nameof(HasDetailsLicenseLink));
            this.RaisePropertyChanged(nameof(DetailsProjectUrl));
            this.RaisePropertyChanged(nameof(HasDetailsProjectUrl));
            this.RaisePropertyChanged(nameof(DetailsDependencies));
            this.RaisePropertyChanged(nameof(HasDetailsDependencies));
            this.RaisePropertyChanged(nameof(DetailsHaveNoDependencies));
        }
    }

    // A license given only as an address is shown as that address.
    public string? DetailsLicense => Details is { } d ? d.License ?? d.LicenseUrl : null;
    public bool HasDetailsLicense => DetailsLicense is not null;
    public string? DetailsLicenseUrl => Details?.LicenseUrl;
    public bool HasDetailsLicenseLink => DetailsLicenseUrl is not null;
    public string? DetailsProjectUrl => Details?.ProjectUrl;
    public bool HasDetailsProjectUrl => DetailsProjectUrl is not null;

    // One line per framework and one per dependency below it.
    public IReadOnlyList<PackageDetailLine> DetailsDependencies => Details is null ? [] :
    [
        .. Details.DependencyGroups.SelectMany(g => g.Dependencies
            .Select(d => new PackageDetailLine(d.VersionRange.Length == 0 ? d.Id : $"{d.Id} ({d.VersionRange})", false))
            .Prepend(new PackageDetailLine(g.TargetFramework, true))),
    ];

    public bool HasDetailsDependencies => DetailsDependencies.Count > 0;

    // Said only when a source did describe the package: otherwise nothing is known.
    public bool DetailsHaveNoDependencies => Details is { DependencyGroups.Count: 0 };

    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    private async Task OpenLinkAsync(string address)
    {
        var allowed = address == DetailsLicenseUrl || address == DetailsProjectUrl;
        if (!allowed || LinkLauncher is not { } launcher || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;
        try
        {
            await launcher(uri);
        }
        catch (Exception ex)
        {
            DetailsError = $"Couldn't open the link: {ex.Message}";
        }
    }

    private string DetailsKey(string id, string? version) => id + "|" + (version ?? "");

    // Asks the sources about the selected package at the selected version, off the UI thread; an answer
    // for a package or version no longer selected is dropped.
    private void LoadDetails()
    {
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;
        var generation = ++_detailsGeneration;

        DetailsError = null;
        if (_detailsId is null)
        {
            IsLoadingDetails = false;
            Details = null;
            PendingDetails = Task.CompletedTask;
            return;
        }

        var id = _detailsId;
        var version = SelectedVersion;
        if (_detailsCache.TryGetValue(DetailsKey(id, version), out var cached))
        {
            IsLoadingDetails = false;
            Details = cached;
            PendingDetails = Task.CompletedTask;
            return;
        }

        Details = null;
        _detailsCts = new CancellationTokenSource();
        PendingDetails = LoadDetailsAsync(id, version, generation, _detailsCts.Token);
    }

    private async Task LoadDetailsAsync(string id, string? version, int generation, CancellationToken ct)
    {
        IsLoadingDetails = true;
        PackageDetails? details;
        try
        {
            details = await Task.Run(() => _search.GetDetailsAsync(id, version, ct), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The card still works without it; adding the package never waits for this.
            if (generation != _detailsGeneration || _disposed) return;
            DetailsError = $"Couldn't load the package details: {ex.Message}";
            IsLoadingDetails = false;
            return;
        }

        if (generation != _detailsGeneration || _disposed) return;
        _detailsCache[DetailsKey(id, version)] = details;
        Details = details;
        IsLoadingDetails = false;
    }

    // Awaited by tests; the UI never needs them.
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;
    internal Task PendingVersions { get; private set; } = Task.CompletedTask;
    internal Task PendingDetails { get; private set; } = Task.CompletedTask;

    // ── References ─────────────────────────────────────────────────────────────

    public ObservableCollection<DependencyEntryViewModel> References { get; } = [];
    public bool HasReferences => References.Count > 0;
    public bool ShowNoReferencesHint => HasFrontMatter && !HasReferences;

    public string NewReference
    {
        get => _newReference;
        set
        {
            this.RaiseAndSetIfChanged(ref _newReference, value ?? "");
            ReferenceError = null;
        }
    }

    public string? ReferenceError
    {
        get => _referenceError;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _referenceError, value);
            this.RaisePropertyChanged(nameof(HasReferenceError));
        }
    }

    public bool HasReferenceError => ReferenceError is not null;

    public DependencyEntryViewModel? SelectedReference
    {
        get => _selectedReference;
        set => this.RaiseAndSetIfChanged(ref _selectedReference, value);
    }

    // ── Namespace imports ──────────────────────────────────────────────────────

    // Edited as plain text, one namespace per line; parsed by ParseNamespaces on every edit (for the
    // inline error) and when comparing/applying.
    public string NamespacesText
    {
        get => _namespacesText;
        set
        {
            this.RaiseAndSetIfChanged(ref _namespacesText, value ?? "");
            NamespaceError = ParseNamespaces(_namespacesText).Error;
            UpdateHasChanges();
        }
    }

    public string? NamespaceError
    {
        get => _namespaceError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _namespaceError, value);
            this.RaisePropertyChanged(nameof(HasNamespaceError));
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool HasNamespaceError => NamespaceError is not null;

    // ── Compilation ────────────────────────────────────────────────────────────

    // C# only: Visual Basic has no unsafe code, so its scripts get no such option.
    public bool HasUnsafeOption => _original?.Language == RpadLanguage.CSharp;

    /// <summary>The Connections tab: the script's <c>connections:</c>.</summary>
    public ScriptConnectionsViewModel Connections { get; }

    /// <summary>The Arguments tab: typed declarations, their named sets and the one runs use.</summary>
    public ScriptArgumentsViewModel Arguments { get; } = new();

    public bool AllowUnsafe
    {
        get => _allowUnsafe;
        set
        {
            this.RaiseAndSetIfChanged(ref _allowUnsafe, value);
            UpdateHasChanges();
        }
    }

    private bool _allowUnsafe;

    // ── Commands ───────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ClearSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> RetrySearchCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryLocalCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryVersionsCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousPageCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdatePackageCommand { get; }
    public ReactiveCommand<Unit, Unit> RemovePackageCommand { get; }
    public ReactiveCommand<PackageListItemViewModel, Unit> AddRowCommand { get; }
    public ReactiveCommand<PackageListItemViewModel, Unit> UpdateRowCommand { get; }
    public ReactiveCommand<PackageListItemViewModel, Unit> RemoveRowCommand { get; }
    public ReactiveCommand<Unit, Unit> AddReferenceCommand { get; }

    // ── Constructor ────────────────────────────────────────────────────────────

    public DependenciesWindowViewModel(IPackageSearchService search, ILocalPackageCacheService cache,
        INuGetSourceStore sources, IConnectionStore? connections = null, ISecretStore? secrets = null,
        ConnectionDriverRegistry? connectionDrivers = null, ConnectionSchemas? connectionSchemas = null)
        : this(search, cache, sources, DefaultSearchDebounce, connections, secrets, connectionDrivers, connectionSchemas)
    {
    }

    internal DependenciesWindowViewModel(IPackageSearchService search, ILocalPackageCacheService cache,
        INuGetSourceStore sources, TimeSpan searchDebounce, IConnectionStore? connections = null, ISecretStore? secrets = null,
        ConnectionDriverRegistry? connectionDrivers = null, ConnectionSchemas? connectionSchemas = null)
    {
        Connections = new ScriptConnectionsViewModel(connections, secrets, connectionDrivers, connectionSchemas);
        Connections.UsePackages(
            () => new HashSet<string>(_draftPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase),
            (id, version) => AddPackage(id, version),
            RemovePackage);
        Connections.Edited += UpdateHasChanges;
        Arguments.Edited += () =>
        {
            UpdateHasChanges();
            this.RaisePropertyChanged(nameof(CanApply));
        };
        OpenLinkCommand = ReactiveCommand.CreateFromTask<string>(OpenLinkAsync).DisposeWith(_disposables);
        _search = search;
        _cache = cache;
        _sources = sources;
        _searchDebounce = searchDebounce;

        References.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasReferences));
            this.RaisePropertyChanged(nameof(ShowNoReferencesHint));
        };

        var hasFrontMatter = this.WhenAnyValue(x => x.HasFrontMatter);

        var canAdd = this.WhenAnyValue(
            x => x.HasFrontMatter, x => x.SelectedPackage, x => x.SelectedScriptVersion, x => x.SelectedVersion,
            (fm, selected, scriptVersion, version) =>
                fm && selected is not null && scriptVersion is null && !string.IsNullOrWhiteSpace(version));
        var canUpdate = this.WhenAnyValue(
            x => x.HasFrontMatter, x => x.SelectedPackage, x => x.SelectedScriptVersion, x => x.SelectedVersion,
            (fm, selected, scriptVersion, version) =>
                fm && selected is not null && scriptVersion is not null && !string.IsNullOrWhiteSpace(version)
                && !string.Equals(scriptVersion, version, StringComparison.Ordinal));
        var canRemove = this.WhenAnyValue(
            x => x.HasFrontMatter, x => x.SelectedPackage, x => x.SelectedScriptVersion,
            (fm, selected, scriptVersion) => fm && selected is not null && scriptVersion is not null);

        ClearSearchCommand = ReactiveCommand.Create(() => { SearchText = ""; }).DisposeWith(_disposables);
        RetrySearchCommand = ReactiveCommand.Create(() => TriggerSearch(immediate: true, resetPage: false),
            this.WhenAnyValue(x => x.HasSearchError)).DisposeWith(_disposables);
        RetryLocalCommand = ReactiveCommand.Create(RetryLocal, this.WhenAnyValue(x => x.HasLocalError))
            .DisposeWith(_disposables);
        RetryVersionsCommand = ReactiveCommand.Create(LoadVersions, this.WhenAnyValue(x => x.HasVersionsError))
            .DisposeWith(_disposables);
        NextPageCommand = ReactiveCommand.Create(NextPage, this.WhenAnyValue(x => x.HasNextPage))
            .DisposeWith(_disposables);
        PreviousPageCommand = ReactiveCommand.Create(PreviousPage, this.WhenAnyValue(x => x.HasPreviousPage))
            .DisposeWith(_disposables);
        AddPackageCommand = ReactiveCommand.Create(AddSelectedPackage, canAdd).DisposeWith(_disposables);
        UpdatePackageCommand = ReactiveCommand.Create(UpdateSelectedPackage, canUpdate).DisposeWith(_disposables);
        RemovePackageCommand = ReactiveCommand.Create(RemoveSelectedPackage, canRemove).DisposeWith(_disposables);
        AddRowCommand = ReactiveCommand.Create<PackageListItemViewModel>(AddRowPackage).DisposeWith(_disposables);
        UpdateRowCommand = ReactiveCommand.Create<PackageListItemViewModel>(UpdateRowPackage).DisposeWith(_disposables);
        RemoveRowCommand = ReactiveCommand.Create<PackageListItemViewModel>(RemoveRowPackage).DisposeWith(_disposables);
        AddReferenceCommand = ReactiveCommand.Create(AddReferenceFromInput, hasFrontMatter).DisposeWith(_disposables);

        ReloadSources();
        RebuildLocal();
        UpdateOnlineState();
    }

    // ── Front matter / draft ───────────────────────────────────────────────────

    // Fed by the active tab's FrontMatter stream (WindowService.ShowDependenciesAsync), so it can go
    // null -> loaded while the dialog is open. Re-sending the same object keeps the draft and selection.
    public void SetFrontMatter(RpadFrontMatter? fm)
    {
        if (_disposed || ReferenceEquals(fm, _original)) return;

        _original = fm;
        _applied = false;

        _draftPackages.Clear();
        if (fm?.Packages is { } packages)
            foreach (var (id, package) in packages)
                _draftPackages.Add(new DraftPackage(id, package.Version));

        ResetEntries(References, fm?.References, RemoveReference);
        _namespacesText = fm?.Namespaces is { Count: > 0 } namespaces
            ? string.Join(Environment.NewLine, namespaces)
            : "";
        ReferenceError = null;
        NamespaceError = null;
        _allowUnsafe = fm?.AllowUnsafe ?? false;
        Connections.Load(fm);
        Arguments.Load(fm);

        this.RaisePropertyChanged(nameof(HasFrontMatter));
        this.RaisePropertyChanged(nameof(HasUnsafeOption));
        this.RaisePropertyChanged(nameof(AllowUnsafe));
        this.RaisePropertyChanged(nameof(ShowNoReferencesHint));
        this.RaisePropertyChanged(nameof(NamespacesText));
        OnDraftPackagesChanged();
    }

    private static void ResetEntries(ObservableCollection<DependencyEntryViewModel> target, List<string>? source,
        Action<DependencyEntryViewModel> remove)
    {
        target.Clear();
        if (source is null) return;
        foreach (var value in source)
            target.Add(new DependencyEntryViewModel(value, remove));
    }

    // Copies the draft's packages/references/namespaces/arguments (and, for C#, the unsafe setting) into the
    // original front matter — the only place it is ever mutated. Everything else on it (version,
    // language, kind, target SDK) is left alone. Applies at most once and only when something actually changed.
    public bool ApplyChanges()
    {
        // An invalid namespace line blocks applying (OK is disabled too) rather than being dropped.
        if (_disposed || _applied || _original is not { } fm || HasNamespaceError || Arguments.HasError || !ComputeHasChanges()) return false;
        _applied = true;

        var previous = fm.Packages is null
            ? new Dictionary<string, Package>()
            : new Dictionary<string, Package>(fm.Packages);
        var packages = fm.Packages ??= new Dictionary<string, Package>();
        packages.Clear();
        foreach (var draft in _draftPackages)
            packages[draft.Id] = previous.TryGetValue(draft.Id, out var old) && old.Version == draft.Version
                ? old
                : new Package(draft.Version);

        fm.References = CopyBack(fm.References, References.Select(e => e.Value));
        fm.Namespaces = CopyBack(fm.Namespaces, ParseNamespaces(_namespacesText).Names);
        if (HasUnsafeOption) fm.AllowUnsafe = _allowUnsafe;
        fm.Connections = Connections.ToList();
        Arguments.ApplyTo(fm);
        return true;
    }

    private static List<string>? CopyBack(List<string>? original, IEnumerable<string> draft)
    {
        var values = draft.ToList();
        if (original is null && values.Count == 0) return null;
        original ??= [];
        original.Clear();
        original.AddRange(values);
        return original;
    }

    private bool ComputeHasChanges()
    {
        if (_original is not { } fm) return false;

        var originalPackages = fm.Packages?.ToList() ?? [];
        var packagesChanged = originalPackages.Count != _draftPackages.Count
                              || originalPackages.Zip(_draftPackages).Any(p =>
                                  p.First.Key != p.Second.Id || p.First.Value.Version != p.Second.Version);

        return packagesChanged
               || !(fm.References ?? []).SequenceEqual(References.Select(r => r.Value), StringComparer.Ordinal)
               || !(fm.Namespaces ?? []).SequenceEqual(ParseNamespaces(_namespacesText).Names, StringComparer.Ordinal)
               || (HasUnsafeOption && fm.AllowUnsafe != _allowUnsafe)
               || Connections.HasChanges
               || Arguments.HasChanges;
    }

    private void UpdateHasChanges() => HasChanges = ComputeHasChanges();

    private DraftPackage? FindDraft(string id)
        => _draftPackages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));


    // ── Package actions ────────────────────────────────────────────────────────

    // Add only records the package in the script; restore happens on the next build/run.
    // Never adds a second entry differing only by case; re-adding the same package is a no-op.
    private void AddPackage(string id, string? version)
    {
        if (!HasFrontMatter || string.IsNullOrWhiteSpace(version) || FindDraft(id) is not null) return;
        _draftPackages.Add(new DraftPackage(id, version.Trim()));
        OnDraftPackagesChanged();
    }

    private void UpdatePackage(string id, string? version)
    {
        if (!HasFrontMatter || string.IsNullOrWhiteSpace(version) || FindDraft(id) is not { } draft) return;
        draft.Version = version.Trim();
        OnDraftPackagesChanged();
    }

    private void RemovePackage(string id)
    {
        if (!HasFrontMatter || FindDraft(id) is not { } draft) return;
        _draftPackages.Remove(draft);
        OnDraftPackagesChanged();
    }

    // Row buttons use the row's own version (latest from the feed, or the cached one); the details
    // column uses the version picked there.
    private void AddRowPackage(PackageListItemViewModel row) => AddPackage(row.Id, row.Version);
    private void UpdateRowPackage(PackageListItemViewModel row) => UpdatePackage(row.Id, row.Version);
    private void RemoveRowPackage(PackageListItemViewModel row) => RemovePackage(row.Id);

    private void AddSelectedPackage()
    {
        if (SelectedPackage is { } item) AddPackage(item.Id, SelectedVersion);
    }

    private void UpdateSelectedPackage()
    {
        if (SelectedPackage is { } item) UpdatePackage(item.Id, SelectedVersion);
    }

    private void RemoveSelectedPackage()
    {
        if (SelectedPackage is { } item) RemovePackage(item.Id);
    }

    private void OnDraftPackagesChanged()
    {
        Connections.OnPackagesChanged();
        foreach (var item in OnlinePackages)
        {
            item.ScriptVersion = FindDraft(item.Id)?.Version;
            item.IsEditable = HasFrontMatter;
        }
        RebuildLocal();

        SelectedScriptVersion = _detailsId is null ? null : FindDraft(_detailsId)?.Version;
        UpdateHasChanges();
    }

    // ── Local column ───────────────────────────────────────────────────────────

    // The script's packages first, then cached packages that aren't in the script.
    private void RebuildLocal()
    {
        EnsureCacheLoaded();

        var filter = LocalFilter.Trim();
        bool Matches(string id) => filter.Length == 0 || id.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var items = new List<PackageListItemViewModel>();
        foreach (var p in _draftPackages.Where(p => Matches(p.Id)))
        {
            var known = FindOnlineResult(p.Id);
            items.Add(new PackageListItemViewModel(p.Id, p.Version, known?.Description ?? "",
                known?.TotalDownloads ?? 0, p.Version, HasFrontMatter));
        }
        foreach (var c in (_cachedPackages ?? []).Where(c => FindDraft(c.Id) is null && Matches(c.Id)))
        {
            var known = FindOnlineResult(c.Id);
            items.Add(new PackageListItemViewModel(c.Id, c.LatestVersion, known?.Description ?? "",
                known?.TotalDownloads ?? 0, null, HasFrontMatter));
        }

        var selectedId = _selectedLocalPackage?.Id;
        _rebuildingLocal = true;
        try
        {
            LocalPackages = items;
            this.RaisePropertyChanged(nameof(LocalPackages));
            this.RaisePropertyChanged(nameof(HasLocalPackages));
        }
        finally
        {
            _rebuildingLocal = false;
        }

        // Keep the same package selected in this list when it's still there.
        _selectedLocalPackage = selectedId is null ? null : FindById(LocalPackages, selectedId);
        this.RaisePropertyChanged(nameof(SelectedLocalPackage));

        LocalMessage = LocalPackages.Count > 0
            ? null
            : filter.Length > 0
                ? "No packages in this script or the local cache match the filter."
                : _draftPackages.Count == 0 && (_cachedPackages?.Count ?? 0) == 0 && LocalError is null
                    ? "This script has no packages and the local package cache is empty."
                    : null;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoadAttempted) return;
        _cacheLoadAttempted = true;
        try
        {
            _cachedPackages = _cache.GetCachedPackages() ?? [];
            LocalError = null;
        }
        catch (Exception ex)
        {
            _cachedPackages = null;
            LocalError = $"Couldn't read the local package cache: {ex.Message}";
        }
    }

    private void RetryLocal()
    {
        _cacheLoadAttempted = false;
        RebuildLocal();
    }

    private void ClearLocalSelection()
    {
        if (_selectedLocalPackage is null) return;
        _selectedLocalPackage = null;
        this.RaisePropertyChanged(nameof(SelectedLocalPackage));
    }

    // ── Online column ──────────────────────────────────────────────────────────

    private void RebuildOnline()
    {
        var selectedId = _selectedOnlinePackage?.Id;
        _rebuildingOnline = true;
        try
        {
            OnlinePackages = _onlineResults
                .Select(r => new PackageListItemViewModel(r.Id, r.Version, r.Description, r.TotalDownloads,
                    FindDraft(r.Id)?.Version, HasFrontMatter))
                .ToList();
            this.RaisePropertyChanged(nameof(OnlinePackages));
            this.RaisePropertyChanged(nameof(HasOnlinePackages));
            this.RaisePropertyChanged(nameof(ShowOnlineLoader));
        }
        finally
        {
            _rebuildingOnline = false;
        }

        _selectedOnlinePackage = selectedId is null ? null : FindById(OnlinePackages, selectedId);
        this.RaisePropertyChanged(nameof(SelectedOnlinePackage));
        UpdateOnlineState();
    }

    private void UpdateOnlineState()
    {
        var query = SearchText.Trim();
        OnlineMessage =
            SearchError is not null && !IsSearching ? null
            : ShowOnlineLoader ? null // the loader says it
            : query.Length == 0 ? $"Type a package name to search {DescribeSelectedSource()}."
            : OnlinePackages.Count == 0 && _onlineQuery == query ? $"No packages found for \"{query}\"."
            : null;
        this.RaisePropertyChanged(nameof(HasNextPage));
        this.RaisePropertyChanged(nameof(HasPreviousPage));
    }

    private string DescribeSelectedSource() => SelectedSourceOption.SourceName is { } name
        ? $"\"{name}\""
        : "all enabled package sources";

    private PackageSearchResult? FindOnlineResult(string id)
        => _onlineResults.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    private static PackageListItemViewModel? FindById(IEnumerable<PackageListItemViewModel> items, string id)
        => items.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private void ClearOnlineSelection()
    {
        if (_selectedOnlinePackage is null) return;
        _selectedOnlinePackage = null;
        this.RaisePropertyChanged(nameof(SelectedOnlinePackage));
    }

    // ── Package sources ────────────────────────────────────────────────────────

    // Re-reads RunPad's NuGet.Config (after the configurator saved it, or on open). Keeps the selected
    // source when it is still enabled, otherwise falls back to "All sources", and re-runs the search.
    public void ReloadSources()
    {
        var previous = _selectedSourceOption.SourceName;

        IReadOnlyList<NuGetSource> sources;
        try
        {
            sources = _sources.Load() ?? [];
            SourcesError = null;
        }
        catch (Exception ex)
        {
            sources = [];
            SourcesError = $"Couldn't read the package source configuration: {ex.Message}";
        }

        _rebuildingSources = true;
        try
        {
            SourceOptions.Clear();
            SourceOptions.Add(PackageSourceOption.AllSources);
            foreach (var source in sources.Where(s => s.IsEnabled))
                SourceOptions.Add(PackageSourceOption.ForSource(source.Name));
            SourceOptions.Add(PackageSourceOption.Configure);
        }
        finally
        {
            _rebuildingSources = false;
        }

        _selectedSourceOption = previous is null
            ? PackageSourceOption.AllSources
            : SourceOptions.FirstOrDefault(o => string.Equals(o.SourceName, previous, StringComparison.OrdinalIgnoreCase))
              ?? PackageSourceOption.AllSources;
        this.RaisePropertyChanged(nameof(SelectedSourceOption));

        // Another source may describe a package differently.
        _detailsCache.Clear();
        if (_detailsId is not null) LoadDetails();

        if (SearchText.Trim().Length > 0)
            TriggerSearch(immediate: true);
        else
            UpdateOnlineState();
    }

    // The configurator's ViewModel, over the same store this window searches with.
    internal NuGetSourcesWindowViewModel CreateSourcesEditor() => new(_sources);

    // ── Search ─────────────────────────────────────────────────────────────────

    // Enter in the search box: skip the debounce.
    public void SearchNow()
    {
        if (!_disposed)
            TriggerSearch(immediate: true);
    }

    private void NextPage()
    {
        if (!HasNextPage) return;
        _skip += PackageSearchOptions.DefaultPageSize;
        ClearPageForLoading();
        TriggerSearch(immediate: true, resetPage: false);
    }

    private void PreviousPage()
    {
        if (!HasPreviousPage) return;
        _skip = Math.Max(0, _skip - PackageSearchOptions.DefaultPageSize);
        ClearPageForLoading();
        TriggerSearch(immediate: true, resetPage: false);
    }

    // The old page must not stay on screen while the next one loads: empty the list (the loader shows)
    // and disable paging until the new page arrives, so a second click can't skip a page blindly.
    private void ClearPageForLoading()
    {
        _onlineResults = [];
        _onlineQuery = null;
        _hasNextPage = false;
        RebuildOnline();
    }

    private bool IsCurrentSearch(int generation) => !_disposed && generation == _searchGeneration;

    // Cancels the pending search and makes any late answer from it stale, even from a service
    // that ignores the token.
    private void InvalidateSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        ++_searchGeneration;
        IsSearching = false;
        PendingSearch = Task.CompletedTask;
    }

    // A new query, source or prerelease setting starts again from the first page.
    private void TriggerSearch(bool immediate, bool resetPage = true)
    {
        InvalidateSearch();
        var generation = _searchGeneration;
        if (resetPage)
            _skip = 0;

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            _onlineResults = [];
            _onlineQuery = null;
            _hasNextPage = false;
            SearchError = null;
            RebuildOnline();
            return;
        }

        var options = new PackageSearchOptions(SelectedSourceOption.SourceName, IncludePrerelease, _skip);
        _searchCts = new CancellationTokenSource();
        PendingSearch = SearchAsync(query, options, generation, immediate, _searchCts.Token);
    }

    private async Task SearchAsync(string query, PackageSearchOptions options, int generation, bool immediate,
        CancellationToken ct)
    {
        if (!immediate && _searchDebounce > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_searchDebounce, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!IsCurrentSearch(generation)) return;

        SearchError = null;
        IsSearching = true;
        UpdateOnlineState();

        PackageSearchPage page;
        string? error = null;
        try
        {
            // Off the UI thread: the NuGet client does real synchronous work before its first await
            // (config + credentials, service index, JSON) that otherwise stalls typing in the box.
            page = await Task.Run(() => _search.SearchAsync(query, options, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            page = new PackageSearchPage([], HasMore: false);
            error = $"Search failed: {ex.Message}";
        }

        if (!IsCurrentSearch(generation)) return;

        _onlineResults = page.Results;
        _onlineQuery = query;
        _hasNextPage = page.HasMore;
        SearchError = error;
        IsSearching = false;
        RebuildOnline();
    }

    // ── Selection / versions ───────────────────────────────────────────────────

    private void SetSelectedPackage(PackageListItemViewModel? item)
    {
        var sameId = item is not null && _detailsId is not null
                     && string.Equals(item.Id, _detailsId, StringComparison.OrdinalIgnoreCase);

        _selectedPackage = item;
        this.RaisePropertyChanged(nameof(SelectedPackage));
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(DetailsTitle));
        this.RaisePropertyChanged(nameof(DetailsDescription));
        this.RaisePropertyChanged(nameof(DetailsDownloads));
        this.RaisePropertyChanged(nameof(HasDetailsDownloads));

        // The same package picked in the other list or re-listed: keep its versions and choice.
        if (sameId) return;

        _detailsId = item?.Id;
        _serverVersions = null;
        SelectedScriptVersion = item is null ? null : FindDraft(item.Id)?.Version;

        // A different package: its versions replace the old list entirely.
        var known = KnownVersions(item);
        _selectedVersion = null;
        ReplaceVersions(known);
        _selectingPackage = true;
        try { SelectedVersion = SelectedScriptVersion ?? known.FirstOrDefault(); }
        finally { _selectingPackage = false; }
        LoadVersions();
        LoadDetails();
    }

    // Versions available without the network: the script's own value (which may be "latest" or a
    // range), the version the list row came with, and the locally cached one.
    private List<string> KnownVersions(PackageListItemViewModel? item)
    {
        if (item is null) return [];
        var cached = _cachedPackages?
            .FirstOrDefault(c => string.Equals(c.Id, item.Id, StringComparison.OrdinalIgnoreCase))?.LatestVersion;
        return new[] { FindDraft(item.Id)?.Version, item.Version, cached }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // SemVer: a "-" before any "+build" metadata marks a prerelease.
    internal static bool IsPrerelease(string version)
    {
        var plus = version.IndexOf('+');
        return (plus < 0 ? version : version[..plus]).Contains('-');
    }

    // Known versions always stay selectable (first, when the server doesn't list them); server versions
    // follow Include Prerelease, newest first.
    private List<string> ComposeVersions()
    {
        var server = _serverVersions ?? [];
        var known = KnownVersions(SelectedPackage);
        var extras = known.Where(v => !server.Contains(v, StringComparer.OrdinalIgnoreCase));
        var visibleServer = server.Where(v =>
            IncludePrerelease || !IsPrerelease(v) || known.Contains(v, StringComparer.OrdinalIgnoreCase));
        return extras.Concat(visibleServer).ToList();
    }

    private void RefreshVersions()
    {
        if (_serverVersions is null || SelectedPackage is null) return;
        ReplaceVersions(ComposeVersions());
        if (SelectedVersion is null && AvailableVersions.Count > 0)
            SelectedVersion = AvailableVersions[0];
    }

    // Never removes the selected version: a ComboBox whose selected item is cleared out from under it
    // drops the selection and doesn't pick it back up. Everything else is removed and the new list
    // is built around the kept item.
    private void ReplaceVersions(IReadOnlyList<string> versions)
    {
        var keep = SelectedVersion;
        var desired = versions.ToList();
        if (keep is not null && !desired.Contains(keep, StringComparer.Ordinal))
            desired.Insert(0, keep);

        _rebuildingVersions = true;
        try
        {
            for (var i = AvailableVersions.Count - 1; i >= 0; i--)
                if (!string.Equals(AvailableVersions[i], keep, StringComparison.Ordinal))
                    AvailableVersions.RemoveAt(i);

            for (var i = 0; i < desired.Count; i++)
                if (i >= AvailableVersions.Count || !string.Equals(AvailableVersions[i], desired[i], StringComparison.Ordinal))
                    AvailableVersions.Insert(i, desired[i]);
        }
        finally
        {
            _rebuildingVersions = false;
        }

        this.RaisePropertyChanged(nameof(SelectedVersion));
    }

    private bool IsCurrentVersions(int generation) => !_disposed && generation == _versionsGeneration;

    private void LoadVersions()
    {
        _versionsCts?.Cancel();
        _versionsCts?.Dispose();
        _versionsCts = null;
        var generation = ++_versionsGeneration;

        VersionsError = null;
        if (_detailsId is null)
        {
            IsLoadingVersions = false;
            PendingVersions = Task.CompletedTask;
            return;
        }

        _versionsCts = new CancellationTokenSource();
        PendingVersions = LoadVersionsAsync(_detailsId, generation, _versionsCts.Token);
    }

    private async Task LoadVersionsAsync(string id, int generation, CancellationToken ct)
    {
        IsLoadingVersions = true;

        IReadOnlyList<string> versions;
        try
        {
            versions = await Task.Run(() => _search.GetVersionsAsync(id, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Known versions stay selectable — a failed lookup never blocks adding them.
            if (!IsCurrentVersions(generation)) return;
            VersionsError = $"Couldn't load versions: {ex.Message}";
            IsLoadingVersions = false;
            return;
        }

        if (!IsCurrentVersions(generation)) return;

        _serverVersions = versions;
        ReplaceVersions(ComposeVersions());
        if (SelectedVersion is null && AvailableVersions.Count > 0)
            SelectedVersion = AvailableVersions[0];
        IsLoadingVersions = false;
    }

    // ── References ─────────────────────────────────────────────────────────────

    private void AddReferenceFromInput()
    {
        if (AddReference(NewReference))
            NewReference = "";
    }

    private static readonly HashSet<string> ProjectFileExtensions =
        new([".csproj", ".vbproj", ".fsproj"], StringComparer.OrdinalIgnoreCase);

    // Stored exactly as entered (macros/relative paths kept); nothing is compiled or copied here.
    internal bool AddReference(string? value)
    {
        if (!HasFrontMatter) return false;

        var reference = value?.Trim() ?? "";
        if (reference.Length == 0)
        {
            ReferenceError = "Enter the path to a .dll.";
            return false;
        }

        // Scripts compile in-process with Roslyn (no MSBuild), which can't build another project;
        // project references are to be redesigned.
        if (ProjectFileExtensions.Contains(Path.GetExtension(reference)))
        {
            ReferenceError = "Project references aren't supported. Reference the project's built .dll instead.";
            return false;
        }

        if (References.Any(r => string.Equals(r.Value, reference, StringComparison.Ordinal)))
        {
            ReferenceError = $"\"{reference}\" is already referenced.";
            return false;
        }

        References.Add(new DependencyEntryViewModel(reference, RemoveReference));
        ReferenceError = null;
        UpdateHasChanges();
        return true;
    }

    private void RemoveReference(DependencyEntryViewModel entry)
    {
        if (!HasFrontMatter) return;
        References.Remove(entry);
        UpdateHasChanges();
    }

    // ── Namespace imports ──────────────────────────────────────────────────────

    private static readonly string[] ImportPrefixes = ["global using ", "using ", "Imports "];

    // One namespace per line. Deliberately no identifier grammar (namespaces are case-sensitive and
    // may use @-escapes): blank lines are skipped, a pasted `using X;` / `global using X;` /
    // `Imports X` is reduced to the name, exact duplicates collapse, and only a line still containing
    // whitespace is rejected.
    internal static (List<string> Names, string? Error) ParseNamespaces(string text)
    {
        var names = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.EndsWith(';')) line = line[..^1].TrimEnd();
            foreach (var prefix in ImportPrefixes)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                line = line[prefix.Length..].TrimStart();
                break;
            }

            if (line.Length == 0) continue;
            if (line.Any(char.IsWhiteSpace))
                return (names, $"\"{line}\" is not a namespace. Enter one namespace per line, e.g. System.Text.Json.");
            if (!names.Contains(line, StringComparer.Ordinal))
                names.Add(line);
        }
        return (names, null);
    }

    // ── Dispose ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _versionsCts?.Cancel();
        _versionsCts?.Dispose();
        _versionsCts = null;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;

        foreach (var entry in References)
            entry.RemoveCommand.Dispose();
        _disposables.Dispose();
        GC.SuppressFinalize(this);
    }
}
