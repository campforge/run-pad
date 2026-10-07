namespace RunPad.Contracts;

public sealed record PackageSearchResult(
    string Id,
    string Version,
    string Description,
    long TotalDownloads);

public interface IPackageSearchService
{
    // Throws when every configured source fails, so a failure isn't reported as "no packages found".
    Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, CancellationToken ct = default);

    // Unique versions of one package across the configured sources, newest first. Metadata only —
    // nothing is downloaded or installed. Throws when every configured source fails.
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct = default);
}
