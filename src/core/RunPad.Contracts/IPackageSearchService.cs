namespace DotPad.Contracts;

public sealed record PackageSearchResult(
    string Id,
    string Version,
    string Description,
    long TotalDownloads);

public interface IPackageSearchService
{
    Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, CancellationToken ct = default);
}
