using DotPad.Contracts;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace DotPad.Languages.CSharp;

public sealed class NuGetPackageSearchService : IPackageSearchService
{
    private readonly IReadOnlyList<SourceRepository> _repositories;

    public NuGetPackageSearchService()
    {
        _repositories = LoadRepositories();
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
        var resultsPerSource = await Task.WhenAll(_repositories.Select(async repository =>
        {
            try
            {
                var resource = await repository.GetResourceAsync<PackageSearchResource>(ct);
                return await resource.SearchAsync(query, filter, skip: 0, take: 20, NullLogger.Instance, ct);
            }
            catch
            {
                // One misconfigured/unreachable source (e.g. an offline corporate feed) shouldn't
                // fail the whole search — the other configured sources still get a chance.
                return [];
            }
        }));

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
}
