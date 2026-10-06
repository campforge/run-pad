using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using RunPad.Contracts;

namespace RunPad.Languages.CSharp;

public sealed class NuGetPackageSearchService : IPackageSearchService
{
    // Cap per-source result count — the Dependencies window's search box only ever shows the top
    // handful of matches, and multiple configured feeds are already queried in parallel and merged
    // (ANALYSIS.md #94).
    private const int MaxResultsPerSource = 20;

    private readonly IReadOnlyList<SourceRepository> _repositories;

    public NuGetPackageSearchService()
        : this(LoadRepositories())
    {
    }

    // Tests pass local folder feeds (or deliberately broken sources) instead of the user's nuget.config.
    internal NuGetPackageSearchService(IReadOnlyList<SourceRepository> repositories)
    {
        _repositories = repositories;
    }

    // Reads the user's own nuget.config (same sources `dotnet restore` would use — corporate/private
    // feeds included), same pattern as LocalPackageCacheService.GetCachedPackages(). Falls back to
    // nuget.org only if that fails or the user has no enabled sources configured at all.
    private static IReadOnlyList<SourceRepository> LoadRepositories()
    {
        try
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            var sources = new PackageSourceProvider(settings).LoadPackageSources()
                .Where(s => s.IsEnabled)
                .Select(Repository.Factory.GetCoreV3)
                .ToList();
            if (sources.Count > 0) return sources;
        }
        catch { /* fall through to the default source below */ }

        return [Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json")];
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var filter = new SearchFilter(includePrerelease: false);
        var resultsPerSource = await QueryAllSourcesAsync(async repository =>
        {
            var resource = await repository.GetResourceAsync<PackageSearchResource>(ct)
                           ?? throw new NotSupportedException("The package source does not support search.");
            return await resource.SearchAsync(query, filter, skip: 0, take: MaxResultsPerSource, NullLogger.Instance, ct);
        }, ct);

        return resultsPerSource
            .SelectMany(results => results)
            .GroupBy(r => r.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Identity.Version).First())
            .OrderBy(r => r.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PackageSearchResult(
                Id: r.Identity.Id,
                Version: r.Identity.Version.ToString(),
                Description: r.Description ?? "",
                TotalDownloads: r.DownloadCount ?? 0L))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return [];

        using var cacheContext = new SourceCacheContext();
        var versionsPerSource = await QueryAllSourcesAsync(async repository =>
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(ct)
                           ?? throw new NotSupportedException("The package source does not support version lookup.");
            return await resource.GetAllVersionsAsync(packageId.Trim(), cacheContext, NullLogger.Instance, ct);
        }, ct);

        return versionsPerSource
            .SelectMany(versions => versions)
            .Distinct(VersionComparer.Default)
            .OrderByDescending(v => v, VersionComparer.Default)
            .Select(v => v.ToNormalizedString())
            .ToList();
    }

    // One misconfigured/unreachable source (e.g. an offline corporate feed) shouldn't fail the whole
    // request — the other configured sources still get a chance. Only when every source fails is it
    // an error, and the message deliberately names no source URL (it could carry credentials).
    private async Task<List<IEnumerable<T>>> QueryAllSourcesAsync<T>(
        Func<SourceRepository, Task<IEnumerable<T>>> query, CancellationToken ct)
    {
        var outcomes = await Task.WhenAll(_repositories.Select(async repository =>
        {
            try
            {
                return (Ok: true, Items: await query(repository));
            }
            catch
            {
                return (Ok: false, Items: Enumerable.Empty<T>());
            }
        }));

        ct.ThrowIfCancellationRequested();

        if (outcomes.Length > 0 && outcomes.All(o => !o.Ok))
            throw new InvalidOperationException(outcomes.Length == 1
                ? "The configured package source could not be reached."
                : $"None of the {outcomes.Length} configured package sources could be reached.");

        return outcomes.Where(o => o.Ok).Select(o => o.Items).ToList();
    }
}
