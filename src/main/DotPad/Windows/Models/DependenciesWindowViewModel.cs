using System.Collections.ObjectModel;
using DotPad.Contracts;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.Windows.Models;

public sealed class PackageResultViewModel
{
    public string Id { get; }
    public string Version { get; }
    public string Description { get; }
    public string Downloads { get; }

    public PackageResultViewModel(PackageSearchResult r)
    {
        Id = r.Id;
        Version = r.Version;
        Description = r.Description;
        Downloads = FormatDownloads(r.TotalDownloads);
    }

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B",
        >= 1_000_000     => $"{n / 1_000_000.0:F1}M",
        >= 1_000         => $"{n / 1_000.0:F0}K",
        _                => n.ToString("N0")
    };
}

public class DependenciesWindowViewModel : ViewModelBase
{
    private readonly IPackageSearchService _search;
    private string _searchText = "";
    private bool _isLoading;
    private bool _hasSearched;
    private string? _errorMessage;
    private CancellationTokenSource? _cts;

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

    public DependenciesWindowViewModel(IPackageSearchService search)
    {
        _search = search;
        Results.CollectionChanged += (_, _) => NotifyDerivedState();
    }

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

        try
        {
            var items = await _search.SearchAsync(q, ct);
            foreach (var item in items)
                Results.Add(new PackageResultViewModel(item));
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
}
