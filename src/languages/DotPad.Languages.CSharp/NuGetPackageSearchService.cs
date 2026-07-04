using DotPad.Contracts;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace DotPad.Languages.CSharp;

public sealed class NuGetPackageSearchService : IPackageSearchService
{
    private static readonly SourceRepository _repository =
        Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var resource = await _repository.GetResourceAsync<PackageSearchResource>(ct);
        var filter = new SearchFilter(includePrerelease: false);

        var results = await resource.SearchAsync(
            query, filter, skip: 0, take: 20, NullLogger.Instance, ct);

        return results.Select(r => new PackageSearchResult(
            Id: r.Identity.Id,
            Version: r.Identity.Version.ToString(),
            Description: r.Description ?? "",
            TotalDownloads: r.DownloadCount ?? 0L)).ToList();
    }
}
