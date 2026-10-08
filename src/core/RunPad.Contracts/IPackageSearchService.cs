namespace RunPad.Contracts;

public sealed record PackageSearchResult(
    string Id,
    string Version,
    string Description,
    long TotalDownloads);

/// <param name="Source">Name of one configured source to search, or null for every enabled source.</param>
/// <param name="Skip">Results to skip per source (paging).</param>
/// <param name="Take">Maximum results per source.</param>
public sealed record PackageSearchOptions(
    string? Source = null,
    bool IncludePrerelease = false,
    int Skip = 0,
    int Take = PackageSearchOptions.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
}

/// <param name="HasMore">True when at least one source returned a full page, so a next page may exist.</param>
public sealed record PackageSearchPage(IReadOnlyList<PackageSearchResult> Results, bool HasMore);

public interface IPackageSearchService
{
    // Throws when every queried source fails, so a failure isn't reported as "no packages found".
    Task<PackageSearchPage> SearchAsync(string query, PackageSearchOptions options, CancellationToken ct = default);

    // Unique versions of one package across the enabled sources, newest first, prerelease included.
    // Metadata only — nothing is downloaded or installed. Throws when every source fails.
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct = default);
}
