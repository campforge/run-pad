using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Shared;

namespace RunPad.Windows.Models;

public enum PackageListMode
{
    Browse,
    InScript,
    Cached
}

// ── Package list row ───────────────────────────────────────────────────────────

// One row of the NuGet list — the same shape for search results, the script's own packages and the
// local cache, so selection/details work identically in every mode.
public sealed class PackageListItemViewModel : ReactiveObject
{
    private string? _scriptVersion;

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
            this.RaisePropertyChanged(nameof(IsInScript));
            this.RaisePropertyChanged(nameof(InScriptLabel));
        }
    }

    public bool IsInScript => ScriptVersion is not null;

    public string InScriptLabel => ScriptVersion is null || ScriptVersion == Version
        ? "In script"
        : $"In script: {ScriptVersion}";

    internal PackageListItemViewModel(string id, string version, string description, long totalDownloads,
        string? scriptVersion)
    {
        Id = id;
        Version = version;
        Description = description;
        TotalDownloads = totalDownloads;
        _scriptVersion = scriptVersion;
    }

    internal static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B",
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F0}K",
        _ => n.ToString("N0")
    };
}

// ── References / imports row ───────────────────────────────────────────────────

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
public class DependenciesWindowViewModel : ViewModelBase, IDisposable
{
    // Debounce before firing a NuGet search after the user stops typing — avoids a request per
    // keystroke (ANALYSIS.md #94).
    private static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(350);

    private sealed class DraftPackage(string id, string version)
    {
        public string Id { get; } = id;
        public string Version { get; set; } = version;
    }

    private readonly IPackageSearchService _search;
    private readonly ILocalPackageCacheService _cache;
    private readonly TimeSpan _searchDebounce;
    private readonly CompositeDisposable _disposables = new();

    private RpadFrontMatter? _original;
    private readonly List<DraftPackage> _draftPackages = [];
    private bool _applied;
    private bool _disposed;

    // Browse results and the query they belong to; the local cache is read lazily, once.
    private IReadOnlyList<PackageSearchResult> _browseResults = [];
    private string? _browseQuery;
    private IReadOnlyList<CachedPackageInfo>? _cachedPackages;
    private bool _cacheLoadAttempted;

    // Every request carries the generation it started in; a late answer from an older generation
    // (even one from a service that ignores its CancellationToken) is dropped.
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private CancellationTokenSource? _versionsCts;
    private int _versionsGeneration;

    // While a bound collection is rebuilt the view pushes a null selection back; ignore it and
    // re-assert the VM's own selection afterwards.
    private bool _rebuildingList;
    private bool _rebuildingVersions;

    private PackageListMode _mode = PackageListMode.Browse;
    private string _searchText = "";
    private bool _isSearching;
    private string? _searchError;
    private string? _cacheError;
    private string? _listMessage;
    private string? _listError;
    private PackageListItemViewModel? _selectedPackage;
    private string? _detailsId;
    private string? _selectedScriptVersion;
    private string? _selectedVersion;
    private bool _isLoadingVersions;
    private string? _versionsError;
    private bool _hasChanges;
    private string _newReference = "";
    private string? _referenceError;
    private string _newNamespace = "";
    private string? _namespaceError;
    private DependencyEntryViewModel? _selectedReference;
    private DependencyEntryViewModel? _selectedNamespace;

    // ── Script / draft ─────────────────────────────────────────────────────────

    public bool HasFrontMatter => _original is not null;

    public bool HasChanges
    {
        get => _hasChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasChanges, value);
    }

    // ── Package list ───────────────────────────────────────────────────────────

    public ObservableCollection<PackageListItemViewModel> Packages { get; } = [];

    public PackageListMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            var leavingBrowse = _mode == PackageListMode.Browse;
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsBrowseMode));
            this.RaisePropertyChanged(nameof(IsInScriptMode));
            this.RaisePropertyChanged(nameof(IsCachedMode));
            this.RaisePropertyChanged(nameof(SearchPlaceholder));
            this.RaisePropertyChanged(nameof(ShowSearchProgress));

            // The search box doubles as the local filter, so a search still pending when Browse is
            // left can no longer be trusted to match the text on return: drop it, and on return
            // search again unless the results already belong to the current text.
            if (leavingBrowse)
                InvalidateSearch();

            if (value == PackageListMode.Browse && SearchText.Trim() != (_browseQuery ?? ""))
                TriggerSearch(immediate: true);
            else
                RebuildList();
        }
    }

    // RadioButton.IsChecked mirrors of Mode; the "false" half of a radio switch is ignored.
    public bool IsBrowseMode
    {
        get => Mode == PackageListMode.Browse;
        set { if (value) Mode = PackageListMode.Browse; }
    }

    public bool IsInScriptMode
    {
        get => Mode == PackageListMode.InScript;
        set { if (value) Mode = PackageListMode.InScript; }
    }

    public bool IsCachedMode
    {
        get => Mode == PackageListMode.Cached;
        set { if (value) Mode = PackageListMode.Cached; }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= "";
            if (_searchText == value) return;
            this.RaiseAndSetIfChanged(ref _searchText, value);
            this.RaisePropertyChanged(nameof(HasSearchText));
            if (Mode == PackageListMode.Browse)
                TriggerSearch(immediate: false);
            else
                RebuildList();
        }
    }

    public bool HasSearchText => SearchText.Length > 0;

    public string SearchPlaceholder => Mode switch
    {
        PackageListMode.InScript => "Filter packages in this script",
        PackageListMode.Cached => "Filter cached packages",
        _ => "Search NuGet feeds"
    };

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSearching, value);
            this.RaisePropertyChanged(nameof(ShowSearchProgress));
        }
    }

    public bool ShowSearchProgress => IsSearching && Mode == PackageListMode.Browse;

    public string? SearchError
    {
        get => _searchError;
        private set => this.RaiseAndSetIfChanged(ref _searchError, value);
    }

    public string? CacheError
    {
        get => _cacheError;
        private set => this.RaiseAndSetIfChanged(ref _cacheError, value);
    }

    public bool HasPackages => Packages.Count > 0;

    // Informational state of the list (idle / loading / empty / no script); never an error.
    public string? ListMessage
    {
        get => _listMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _listMessage, value);
            this.RaisePropertyChanged(nameof(ShowListMessage));
        }
    }

    public bool ShowListMessage => ListMessage is not null;

    // Failure of the current mode's source (search or cache), with Retry.
    public string? ListError
    {
        get => _listError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _listError, value);
            this.RaisePropertyChanged(nameof(HasListError));
        }
    }

    public bool HasListError => ListError is not null;

    public PackageListItemViewModel? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (_rebuildingList) return;
            SetSelectedPackage(value);
        }
    }

    // ── Package details ────────────────────────────────────────────────────────

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
            this.RaiseAndSetIfChanged(ref _selectedVersion, value);
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

    // Awaited by tests; the UI never needs them.
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;
    internal Task PendingVersions { get; private set; } = Task.CompletedTask;

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

    public ObservableCollection<DependencyEntryViewModel> Namespaces { get; } = [];
    public bool HasNamespaces => Namespaces.Count > 0;
    public bool ShowNoNamespacesHint => HasFrontMatter && !HasNamespaces;

    public string NewNamespace
    {
        get => _newNamespace;
        set
        {
            this.RaiseAndSetIfChanged(ref _newNamespace, value ?? "");
            NamespaceError = null;
        }
    }

    public string? NamespaceError
    {
        get => _namespaceError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _namespaceError, value);
            this.RaisePropertyChanged(nameof(HasNamespaceError));
        }
    }

    public bool HasNamespaceError => NamespaceError is not null;

    public DependencyEntryViewModel? SelectedNamespace
    {
        get => _selectedNamespace;
        set => this.RaiseAndSetIfChanged(ref _selectedNamespace, value);
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ClearSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryListCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryVersionsCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdatePackageCommand { get; }
    public ReactiveCommand<Unit, Unit> RemovePackageCommand { get; }
    public ReactiveCommand<Unit, Unit> AddReferenceCommand { get; }
    public ReactiveCommand<Unit, Unit> AddNamespaceCommand { get; }

    // ── Constructor ────────────────────────────────────────────────────────────

    public DependenciesWindowViewModel(IPackageSearchService search, ILocalPackageCacheService cache)
        : this(search, cache, DefaultSearchDebounce)
    {
    }

    internal DependenciesWindowViewModel(IPackageSearchService search, ILocalPackageCacheService cache,
        TimeSpan searchDebounce)
    {
        _search = search;
        _cache = cache;
        _searchDebounce = searchDebounce;

        Packages.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasPackages));
        References.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasReferences));
            this.RaisePropertyChanged(nameof(ShowNoReferencesHint));
        };
        Namespaces.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasNamespaces));
            this.RaisePropertyChanged(nameof(ShowNoNamespacesHint));
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
        RetryListCommand = ReactiveCommand.Create(RetryList, this.WhenAnyValue(x => x.HasListError))
            .DisposeWith(_disposables);
        RetryVersionsCommand = ReactiveCommand.Create(LoadVersions, this.WhenAnyValue(x => x.HasVersionsError))
            .DisposeWith(_disposables);
        AddPackageCommand = ReactiveCommand.Create(AddSelectedPackage, canAdd).DisposeWith(_disposables);
        UpdatePackageCommand = ReactiveCommand.Create(UpdateSelectedPackage, canUpdate).DisposeWith(_disposables);
        RemovePackageCommand = ReactiveCommand.Create(RemoveSelectedPackage, canRemove).DisposeWith(_disposables);
        AddReferenceCommand = ReactiveCommand.Create(AddReferenceFromInput, hasFrontMatter).DisposeWith(_disposables);
        AddNamespaceCommand = ReactiveCommand.Create(AddNamespaceFromInput, hasFrontMatter).DisposeWith(_disposables);

        UpdateListState();
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
        ResetEntries(Namespaces, fm?.Namespaces, RemoveNamespace);
        ReferenceError = null;
        NamespaceError = null;

        this.RaisePropertyChanged(nameof(HasFrontMatter));
        this.RaisePropertyChanged(nameof(ShowNoReferencesHint));
        this.RaisePropertyChanged(nameof(ShowNoNamespacesHint));
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

    // Copies the draft's packages/references/namespaces into the original front matter — the only
    // place it is ever mutated. Everything else on it (version, language, kind, target SDK) is left
    // alone. Applies at most once and only when something actually changed.
    public bool ApplyChanges()
    {
        if (_disposed || _applied || _original is not { } fm || !ComputeHasChanges()) return false;
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

        fm.References = CopyBack(fm.References, References);
        fm.Namespaces = CopyBack(fm.Namespaces, Namespaces);
        return true;
    }

    private static List<string>? CopyBack(List<string>? original, IEnumerable<DependencyEntryViewModel> draft)
    {
        var values = draft.Select(e => e.Value).ToList();
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
               || !(fm.Namespaces ?? []).SequenceEqual(Namespaces.Select(n => n.Value), StringComparer.Ordinal);
    }

    private void UpdateHasChanges() => HasChanges = ComputeHasChanges();

    private DraftPackage? FindDraft(string id)
        => _draftPackages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    // ── Package actions ────────────────────────────────────────────────────────

    // Add only records the package in the script; restore happens on the next build/run.
    // Never adds a second entry differing only by case; re-adding the same version is a no-op.
    private void AddSelectedPackage()
    {
        if (!HasFrontMatter || SelectedPackage is not { } item || string.IsNullOrWhiteSpace(SelectedVersion)) return;
        if (FindDraft(item.Id) is not null) return;

        _draftPackages.Add(new DraftPackage(item.Id, SelectedVersion.Trim()));
        OnDraftPackagesChanged();
    }

    private void UpdateSelectedPackage()
    {
        if (!HasFrontMatter || SelectedPackage is not { } item || string.IsNullOrWhiteSpace(SelectedVersion)) return;
        if (FindDraft(item.Id) is not { } draft) return;

        draft.Version = SelectedVersion.Trim();
        OnDraftPackagesChanged();
    }

    private void RemoveSelectedPackage()
    {
        if (!HasFrontMatter || SelectedPackage is not { } item) return;
        if (FindDraft(item.Id) is not { } draft) return;

        _draftPackages.Remove(draft);
        OnDraftPackagesChanged();
    }

    private void OnDraftPackagesChanged()
    {
        if (Mode == PackageListMode.InScript)
            RebuildList();
        else
        {
            foreach (var item in Packages)
                item.ScriptVersion = FindDraft(item.Id)?.Version;
            UpdateListState();
        }

        SelectedScriptVersion = _detailsId is null ? null : FindDraft(_detailsId)?.Version;
        UpdateHasChanges();
    }

    // ── List building ──────────────────────────────────────────────────────────

    private void RebuildList()
    {
        var selectedId = _detailsId;

        _rebuildingList = true;
        try
        {
            Packages.Clear();
            foreach (var item in BuildItems())
                Packages.Add(item);
        }
        finally
        {
            _rebuildingList = false;
        }

        // Keep the same package selected (and its chosen version) when it's still listed.
        var keep = selectedId is null
            ? null
            : Packages.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        SetSelectedPackage(keep);
        UpdateListState();
    }

    private IEnumerable<PackageListItemViewModel> BuildItems()
    {
        var filter = SearchText.Trim();
        bool Matches(string id) => filter.Length == 0 || id.Contains(filter, StringComparison.OrdinalIgnoreCase);

        switch (Mode)
        {
            case PackageListMode.Browse:
                foreach (var r in _browseResults)
                    yield return new PackageListItemViewModel(r.Id, r.Version, r.Description, r.TotalDownloads,
                        FindDraft(r.Id)?.Version);
                break;

            case PackageListMode.InScript:
                foreach (var p in _draftPackages.Where(p => Matches(p.Id)))
                {
                    var known = FindBrowseResult(p.Id);
                    yield return new PackageListItemViewModel(p.Id, p.Version, known?.Description ?? "",
                        known?.TotalDownloads ?? 0, p.Version);
                }
                break;

            case PackageListMode.Cached:
                EnsureCacheLoaded();
                foreach (var c in (_cachedPackages ?? []).Where(c => Matches(c.Id)))
                {
                    var known = FindBrowseResult(c.Id);
                    yield return new PackageListItemViewModel(c.Id, c.LatestVersion, known?.Description ?? "",
                        known?.TotalDownloads ?? 0, FindDraft(c.Id)?.Version);
                }
                break;
        }
    }

    private PackageSearchResult? FindBrowseResult(string id)
        => _browseResults.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    private void UpdateListState()
    {
        string? message = null;
        string? error = null;
        var query = SearchText.Trim();

        switch (Mode)
        {
            case PackageListMode.Browse:
                if (SearchError is not null && !IsSearching)
                    error = SearchError;
                else if (IsSearching && Packages.Count == 0)
                    message = "Searching NuGet feeds…";
                else if (query.Length == 0)
                    message = "Type a package name to search the configured NuGet feeds.";
                else if (Packages.Count == 0 && _browseQuery == query)
                    message = $"No packages found for \"{query}\".";
                break;

            case PackageListMode.InScript:
                if (!HasFrontMatter)
                    message = "No script is open. Open an .rpad script to manage its packages.";
                else if (_draftPackages.Count == 0)
                    message = "This script has no packages yet. Use Browse or Cached to add one.";
                else if (Packages.Count == 0)
                    message = "No packages in this script match the filter.";
                break;

            case PackageListMode.Cached:
                if (CacheError is not null)
                    error = CacheError;
                else if (Packages.Count == 0)
                    message = (_cachedPackages?.Count ?? 0) == 0
                        ? "The local NuGet package cache is empty."
                        : "No cached packages match the filter.";
                break;
        }

        ListMessage = message;
        ListError = error;
    }

    private void RetryList()
    {
        if (Mode == PackageListMode.Browse)
            TriggerSearch(immediate: true);
        else if (Mode == PackageListMode.Cached)
        {
            _cacheLoadAttempted = false;
            RebuildList();
        }
    }

    // ── Local cache ────────────────────────────────────────────────────────────

    private void EnsureCacheLoaded()
    {
        if (_cacheLoadAttempted) return;
        _cacheLoadAttempted = true;
        try
        {
            _cachedPackages = _cache.GetCachedPackages() ?? [];
            CacheError = null;
        }
        catch (Exception ex)
        {
            _cachedPackages = null;
            CacheError = $"Couldn't read the local package cache: {ex.Message}";
        }
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    // Enter in the search box: skip the debounce (Browse only; the other modes filter as you type).
    public void SearchNow()
    {
        if (!_disposed && Mode == PackageListMode.Browse)
            TriggerSearch(immediate: true);
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

    private void TriggerSearch(bool immediate)
    {
        InvalidateSearch();
        var generation = _searchGeneration;

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            _browseResults = [];
            _browseQuery = null;
            SearchError = null;
            if (Mode == PackageListMode.Browse)
                RebuildList();
            else
                UpdateListState();
            return;
        }

        _searchCts = new CancellationTokenSource();
        PendingSearch = SearchAsync(query, generation, immediate, _searchCts.Token);
    }

    private async Task SearchAsync(string query, int generation, bool immediate, CancellationToken ct)
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
        UpdateListState();

        IReadOnlyList<PackageSearchResult> results;
        string? error = null;
        try
        {
            results = await _search.SearchAsync(query, ct);
        }
        catch (Exception ex)
        {
            results = [];
            error = $"Search failed: {ex.Message}";
        }

        if (!IsCurrentSearch(generation)) return;

        _browseResults = results;
        _browseQuery = query;
        SearchError = error;
        IsSearching = false;
        if (Mode == PackageListMode.Browse)
            RebuildList();
        else
            UpdateListState();
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

        // Same package re-listed after a mode switch/filter/re-search: keep its versions and choice.
        if (sameId) return;

        _detailsId = item?.Id;
        SelectedScriptVersion = item is null ? null : FindDraft(item.Id)?.Version;

        // A different package: its versions replace the old list entirely.
        var known = KnownVersions(item);
        _selectedVersion = null;
        ReplaceVersions(known);
        SelectedVersion = SelectedScriptVersion ?? known.FirstOrDefault();
        LoadVersions();
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
            versions = await _search.GetVersionsAsync(id, ct);
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

        var server = versions.ToList();
        var extras = KnownVersions(SelectedPackage)
            .Where(v => !server.Contains(v, StringComparer.OrdinalIgnoreCase));
        ReplaceVersions(extras.Concat(server).ToList());
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

    // Stored exactly as entered (macros/relative paths kept); nothing is compiled or copied here.
    internal bool AddReference(string? value)
    {
        if (!HasFrontMatter) return false;

        var reference = value?.Trim() ?? "";
        if (reference.Length == 0)
        {
            ReferenceError = "Enter the path to a .dll or project file.";
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

    private void AddNamespaceFromInput()
    {
        if (AddNamespace(NewNamespace))
            NewNamespace = "";
    }

    // Deliberately no identifier grammar: only empty values, embedded whitespace (e.g. a pasted
    // "using System.Linq") and exact duplicates are rejected — namespaces are case-sensitive.
    internal bool AddNamespace(string? value)
    {
        if (!HasFrontMatter) return false;

        var name = value?.Trim() ?? "";
        if (name.Length == 0)
        {
            NamespaceError = "Enter a namespace, e.g. System.Text.Json.";
            return false;
        }

        if (name.Any(char.IsWhiteSpace))
        {
            NamespaceError = "A namespace can't contain spaces. Enter just the name, e.g. System.Text.Json.";
            return false;
        }

        if (Namespaces.Any(n => string.Equals(n.Value, name, StringComparison.Ordinal)))
        {
            NamespaceError = $"\"{name}\" is already imported.";
            return false;
        }

        Namespaces.Add(new DependencyEntryViewModel(name, RemoveNamespace));
        NamespaceError = null;
        UpdateHasChanges();
        return true;
    }

    private void RemoveNamespace(DependencyEntryViewModel entry)
    {
        if (!HasFrontMatter) return;
        Namespaces.Remove(entry);
        UpdateHasChanges();
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

        foreach (var entry in References.Concat(Namespaces))
            entry.RemoveCommand.Dispose();
        _disposables.Dispose();
        GC.SuppressFinalize(this);
    }
}
