namespace RunPad.Contracts;

public sealed record PackageSearchResult(
    string Id,
    string Version,
    string Description,
    long TotalDownloads);

/// <param name="Id">The package that is depended on.</param>
/// <param name="VersionRange">The accepted versions in readable form, such as <c>&gt;= 1.0.0</c>; empty when the feed gave none.</param>
public sealed record PackageDependencyInfo(string Id, string VersionRange);

/// <param name="TargetFramework">The framework the dependencies apply to in short form, such as <c>net8.0</c>, or <c>any</c>.</param>
public sealed record PackageDependencyGroupInfo(string TargetFramework, IReadOnlyList<PackageDependencyInfo> Dependencies);

/// <summary>
/// What a feed says about one version of a package beyond the search result. Every part is optional:
/// feeds supply different metadata, and text is bounded and links are web addresses only.
/// </summary>
/// <param name="DependencyGroups">Dependencies per target framework, groups without any left out.</param>
/// <param name="License">The license as text: an SPDX expression, or a note that the package carries a license file.</param>
/// <param name="LicenseUrl">A web address for the license, when the feed has one.</param>
/// <param name="ProjectUrl">The package's project or home page.</param>
public sealed record PackageDetails(
    IReadOnlyList<PackageDependencyGroupInfo> DependencyGroups,
    string? License,
    string? LicenseUrl,
    string? ProjectUrl);

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

    // The dependencies, license and project address one version of a package has in the enabled
    // sources (the newest stable version when <paramref name="version"/> is not a single version);
    // null when no source supplies the package. Metadata only. Throws when every source fails.
    Task<PackageDetails?> GetDetailsAsync(string packageId, string? version, CancellationToken ct = default)
        => Task.FromResult<PackageDetails?>(null);
}
