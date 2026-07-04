using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DotPad.Contracts;
using DotPad.Format.RPad;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.Windows.Models;

// ── Search results ─────────────────────────────────────────────────────────────

public sealed class PackageResultViewModel : ReactiveObject
{
    public string Id { get; }
    public string Version { get; }
    public string Description { get; }
    public string Downloads { get; }

    private bool _isInScript;
    public bool IsInScript
    {
        get => _isInScript;
        set => this.RaiseAndSetIfChanged(ref _isInScript, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    internal PackageResultViewModel(PackageSearchResult r, bool isInScript, Action<PackageResultViewModel> onAdd, IObservable<bool> hasFrontMatter)
    {
        Id = r.Id;
        Version = r.Version;
        Description = r.Description;
        Downloads = FormatDownloads(r.TotalDownloads);
        IsInScript = isInScript;

        var canAdd = Observable.CombineLatest(
            this.WhenAnyValue(x => x.IsInScript).Select(v => !v),
            hasFrontMatter,
            (notInScript, hasFm) => notInScript && hasFm);

        AddCommand = ReactiveCommand.Create(() => onAdd(this), canAdd);
    }

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B",
        >= 1_000_000     => $"{n / 1_000_000.0:F1}M",
        >= 1_000         => $"{n / 1_000.0:F0}K",
        _                => n.ToString("N0")
    };
}

// ── Local cache ────────────────────────────────────────────────────────────────

public sealed class CachedPackageItemViewModel : ReactiveObject
{
    public string Id { get; }
    public string Version { get; }

    private bool _isInScript;
    public bool IsInScript
    {
        get => _isInScript;
        set => this.RaiseAndSetIfChanged(ref _isInScript, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    internal CachedPackageItemViewModel(CachedPackageInfo info, bool isInScript, Action<CachedPackageItemViewModel> onToggle, IObservable<bool> hasFrontMatter)
    {
        Id = info.Id;
        Version = info.LatestVersion;
        IsInScript = isInScript;
        ToggleCommand = ReactiveCommand.Create(() => onToggle(this), hasFrontMatter);
    }
}

// ── Script packages ────────────────────────────────────────────────────────────

public sealed class ScriptPackageItemViewModel
{
    public string Id { get; }
    public string Version { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    internal ScriptPackageItemViewModel(string id, string version, Action<ScriptPackageItemViewModel> remove)
    {
        Id = id;
        Version = version;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }
}

// ── Imports (usings) ───────────────────────────────────────────────────────────

public sealed class UsingItemViewModel
{
    public string Name { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    internal UsingItemViewModel(string name, Action<UsingItemViewModel> remove)
    {
        Name = name;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }
}

// ── Main ViewModel ─────────────────────────────────────────────────────────────

public class DependenciesWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IPackageSearchService _search;
    private readonly ILocalPackageCacheService _cache;
    private RpadFrontMatter? _frontMatter;

    private string _searchText = "";
    private bool _isLoading;
    private bool _hasSearched;
    private string? _errorMessage;
    private string _newUsing = "";
    private CancellationTokenSource? _cts;

    // ── Search ────────────────────────────────────────────────────────────────

    public ObservableCollection<PackageResultViewModel> Results { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            TriggerSearch();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { this.RaiseAndSetIfChanged(ref _isLoading, value); NotifyDerivedState(); }
    }

    public bool HasSearched
    {
        get => _hasSearched;
        private set { this.RaiseAndSetIfChanged(ref _hasSearched, value); NotifyDerivedState(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { this.RaiseAndSetIfChanged(ref _errorMessage, value); NotifyDerivedState(); }
    }

    public bool HasResults => Results.Count > 0;
    public bool HasError => ErrorMessage is not null;
    public bool ShowEmptyState => HasSearched && !IsLoading && !HasResults && !HasError;
    public bool ShowIdleMessage => !HasSearched && !IsLoading;

    // ── Local cache ───────────────────────────────────────────────────────────

    public ObservableCollection<CachedPackageItemViewModel> CacheItems { get; } = [];
    public bool HasCacheItems => CacheItems.Count > 0;

    // ── Script packages ───────────────────────────────────────────────────────

    public ObservableCollection<ScriptPackageItemViewModel> ScriptPackages { get; } = [];
    public bool HasScriptPackages => ScriptPackages.Count > 0;
    public bool HasFrontMatter => _frontMatter is not null;
    public bool ShowNoPackagesHint => HasFrontMatter && !HasScriptPackages;

    // ── Imports ───────────────────────────────────────────────────────────────

    public ObservableCollection<UsingItemViewModel> Usings { get; } = [];
    public bool HasUsings => Usings.Count > 0;
    public bool ShowNoUsingsHint => HasFrontMatter && !HasUsings;

    public string NewUsing
    {
        get => _newUsing;
        set => this.RaiseAndSetIfChanged(ref _newUsing, value);
    }

    public ReactiveCommand<Unit, Unit> AddUsingCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DependenciesWindowViewModel(IPackageSearchService search, ILocalPackageCacheService cache)
    {
        _search = search;
        _cache = cache;

        Results.CollectionChanged += (_, _) => NotifyDerivedState();
        CacheItems.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasCacheItems));
        ScriptPackages.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasScriptPackages));
            this.RaisePropertyChanged(nameof(ShowNoPackagesHint));
        };
        Usings.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasUsings));
            this.RaisePropertyChanged(nameof(ShowNoUsingsHint));
        };

        var canAddUsing = this.WhenAnyValue(
            x => x.NewUsing, x => x.HasFrontMatter,
            (u, hasFm) => !string.IsNullOrWhiteSpace(u) && hasFm);

        AddUsingCommand = ReactiveCommand.Create(ExecuteAddUsing, canAddUsing);
    }

    // ── Front matter injection ────────────────────────────────────────────────

    public void SetFrontMatter(RpadFrontMatter? fm)
    {
        _frontMatter = fm;
        this.RaisePropertyChanged(nameof(HasFrontMatter));
        this.RaisePropertyChanged(nameof(ShowNoPackagesHint));
        this.RaisePropertyChanged(nameof(ShowNoUsingsHint));

        var scriptPackageIds = BuildScriptPackageSet(fm);
        LoadScriptPackages(fm?.Packages);
        LoadCache(scriptPackageIds);
        LoadUsings(fm?.Usings);
    }

    private static HashSet<string> BuildScriptPackageSet(RpadFrontMatter? fm)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fm?.Packages is { } p)
            set.UnionWith(p.Keys);
        return set;
    }

    // Returns an observable of HasFrontMatter suitable for item-level canExecute
    private IObservable<bool> HasFrontMatterObservable()
        => this.WhenAnyValue(x => x.HasFrontMatter);

    // ── Script packages ───────────────────────────────────────────────────────

    private void LoadScriptPackages(Dictionary<string, string>? packages)
    {
        ScriptPackages.Clear();
        if (packages is null) return;
        foreach (var (id, version) in packages)
            ScriptPackages.Add(new ScriptPackageItemViewModel(id, version, RemoveScriptPackage));
    }

    private void AddPackage(string id, string version)
    {
        if (_frontMatter is null) return;
        _frontMatter.Packages ??= new Dictionary<string, string>();
        if (_frontMatter.Packages.ContainsKey(id)) return;

        _frontMatter.Packages[id] = version;
        ScriptPackages.Add(new ScriptPackageItemViewModel(id, version, RemoveScriptPackage));
        SetIsInScript(id, true);
    }

    private void RemoveScriptPackage(ScriptPackageItemViewModel item)
    {
        _frontMatter?.Packages?.Remove(item.Id);
        ScriptPackages.Remove(item);
        SetIsInScript(item.Id, false);
    }

    private void SetIsInScript(string id, bool value)
    {
        foreach (var item in CacheItems)
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                item.IsInScript = value;

        foreach (var item in Results)
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                item.IsInScript = value;
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private void LoadCache(HashSet<string> scriptPackageIds)
    {
        CacheItems.Clear();
        var hasFm = HasFrontMatterObservable();
        try
        {
            foreach (var p in _cache.GetCachedPackages())
                CacheItems.Add(new CachedPackageItemViewModel(p, scriptPackageIds.Contains(p.Id), ToggleCachePackage, hasFm));
        }
        catch { } // BUG-10: ошибки IO при загрузке кэша молча проглатываются — пользователь видит пустой список.
    }

    private void ToggleCachePackage(CachedPackageItemViewModel item)
    {
        if (item.IsInScript)
        {
            var sp = ScriptPackages.FirstOrDefault(
                p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (sp is not null) RemoveScriptPackage(sp);
        }
        else
        {
            AddPackage(item.Id, item.Version);
        }
    }

    // ── Imports ───────────────────────────────────────────────────────────────

    private void LoadUsings(List<string>? usings)
    {
        Usings.Clear();
        if (usings is null) return;
        foreach (var u in usings)
            Usings.Add(new UsingItemViewModel(u, RemoveUsingItem));
    }

    private void ExecuteAddUsing()
    {
        if (_frontMatter is null) return;
        var name = _newUsing.Trim();
        if (string.IsNullOrEmpty(name)) return;

        (_frontMatter.Usings ??= []).Add(name);
        Usings.Add(new UsingItemViewModel(name, RemoveUsingItem));
        NewUsing = "";
    }

    private void RemoveUsingItem(UsingItemViewModel item)
    {
        _frontMatter?.Usings?.Remove(item.Name);
        Usings.Remove(item);
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    private void NotifyDerivedState()
    {
        this.RaisePropertyChanged(nameof(HasResults));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(ShowEmptyState));
        this.RaisePropertyChanged(nameof(ShowIdleMessage));
    }

    private void TriggerSearch()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = SearchAsync(_cts.Token);
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        var q = SearchText.Trim();
        if (string.IsNullOrEmpty(q))
        {
            Results.Clear();
            ErrorMessage = null;
            HasSearched = false;
            return;
        }

        try { await Task.Delay(350, ct); }
        catch (OperationCanceledException) { return; }

        ErrorMessage = null;
        Results.Clear();
        HasSearched = true;
        IsLoading = true;

        var scriptPackageIds = BuildScriptPackageSet(_frontMatter);
        var hasFm = HasFrontMatterObservable();

        try
        {
            var items = await _search.SearchAsync(q, ct);
            foreach (var item in items)
                Results.Add(new PackageResultViewModel(item, scriptPackageIds.Contains(item.Id), r => AddPackage(r.Id, r.Version), hasFm));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
