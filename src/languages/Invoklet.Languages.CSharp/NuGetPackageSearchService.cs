using Invoklet.Contracts;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Invoklet.Languages.CSharp;

public sealed class NuGetPackageSearchService : IPackageSearchService
{
    private readonly INuGetSourceStore? _store;
    private readonly object _lock = new();
    private IReadOnlyList<SourceRepository>? _repositories;

    // Sources come from Invoklet's own NuGet.Config — the same file every script build restores with —
    // and are re-read after the configurator saves it, so no restart is needed.
    public NuGetPackageSearchService(INuGetSourceStore store)
    {
        _store = store;
        store.Changed += () =>
        {
            lock (_lock) _repositories = null;
        };
    }

    // Tests pass local folder feeds (or deliberately broken sources) instead of a config file.
    internal NuGetPackageSearchService(IReadOnlyList<SourceRepository> repositories)
    {
        _repositories = repositories;
    }

    private IReadOnlyList<SourceRepository> Repositories
    {
        get
        {
            lock (_lock)
                return _repositories ??= LoadRepositories(_store!);
        }
    }

    private static IReadOnlyList<SourceRepository> LoadRepositories(INuGetSourceStore store)
    {
        IReadOnlyList<NuGetSource> sources;
        try
        {
            sources = store.Load();
        }
        catch
        {
            sources = [];
        }

        return sources.Where(s => s.IsEnabled).Select(ToRepository).ToList();
    }

    private static SourceRepository ToRepository(NuGetSource source)
    {
        var packageSource = new PackageSource(source.Url, source.Name);
        if (source.HasCredentials)
            packageSource.Credentials = new PackageSourceCredential(
                source.Name, source.Username!, source.Password ?? "", isPasswordClearText: true,
                validAuthenticationTypesText: null);
        return Repository.Factory.GetCoreV3(packageSource);
    }

    public async Task<PackageSearchPage> SearchAsync(string query, PackageSearchOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new PackageSearchPage([], HasMore: false);

        var repositories = options.Source is { } name
            ? Repositories.Where(r => string.Equals(r.PackageSource.Name, name, StringComparison.OrdinalIgnoreCase)).ToList()
            : Repositories;
        if (repositories.Count == 0)
            throw new InvalidOperationException(options.Source is null
                ? "No package source is enabled. Configure package sources first."
                : "The selected package source is not configured or not enabled.");

        var filter = new SearchFilter(options.IncludePrerelease);
        var resultsPerSource = await QueryAllSourcesAsync(repositories, async repository =>
        {
            var resource = await repository.GetResourceAsync<PackageSearchResource>(ct)
                           ?? throw new NotSupportedException("The package source does not support search.");
            return (await resource.SearchAsync(query.Trim(), filter, options.Skip, options.Take, NullLogger.Instance, ct))
                .ToList();
        }, ct);

        var results = resultsPerSource
            .SelectMany(r => r)
            .GroupBy(r => r.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Identity.Version).First())
            .Select(r => new PackageSearchResult(
                Id: r.Identity.Id,
                Version: r.Identity.Version.ToString(),
                Description: r.Description ?? "",
                TotalDownloads: r.DownloadCount ?? 0L))
            .ToList();

        // Keep the feed's own relevance order (nuget.org ranks by popularity) for a single source;
        // merged results from several sources have no common ranking, so order by downloads.
        if (resultsPerSource.Count > 1)
            results = results.OrderByDescending(r => r.TotalDownloads)
                .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new PackageSearchPage(results, HasMore: resultsPerSource.Any(r => r.Count >= options.Take));
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return [];

        using var cacheContext = new SourceCacheContext();
        var versionsPerSource = await QueryAllSourcesAsync(Repositories, async repository =>
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

    public async Task<PackageDetails?> GetDetailsAsync(string packageId, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        using var cacheContext = new SourceCacheContext();
        var perSource = await QueryAllSourcesAsync(Repositories, async repository =>
        {
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(ct)
                           ?? throw new NotSupportedException("The package source does not support package metadata.");
            return (await resource.GetMetadataAsync(packageId.Trim(), includePrerelease: true, includeUnlisted: false,
                cacheContext, NullLogger.Instance, ct)).ToList();
        }, ct);

        var all = perSource.SelectMany(m => m).ToList();
        IPackageSearchMetadata? chosen = null;
        if (NuGetVersion.TryParse(version, out var wanted))
            chosen = all.FirstOrDefault(m => m.Identity.Version == wanted);
        // A range, "latest" or a version the feeds do not list: describe the newest stable release.
        chosen ??= all.Where(m => !m.Identity.Version.IsPrerelease).OrderByDescending(m => m.Identity.Version).FirstOrDefault()
                   ?? all.OrderByDescending(m => m.Identity.Version).FirstOrDefault();
        return chosen is null ? null : ToDetails(chosen);
    }

    // Anything a feed sends is shown to the user, so text is bounded and cleaned and a link must be
    // an http(s) address without credentials; a malformed value is dropped, never an error.
    internal static PackageDetails ToDetails(IPackageSearchMetadata metadata)
    {
        const int MaxGroups = 30, MaxDependencies = 200;
        var groups = new List<PackageDependencyGroupInfo>();
        foreach (var group in (metadata.DependencySets ?? []).Take(MaxGroups))
        {
            var dependencies = (group.Packages ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Take(MaxDependencies)
                .Select(d => new PackageDependencyInfo(Clean(d.Id, 128)!, ReadableRange(d.VersionRange)))
                .ToList();
            if (dependencies.Count > 0)
                groups.Add(new PackageDependencyGroupInfo(
                    group.TargetFramework is null ? "any" : Clean(group.TargetFramework.GetShortFolderName(), 64) ?? "any", dependencies));
        }

        string? license = null;
        var licenseUrl = WebAddress(metadata.LicenseUrl?.AbsoluteUri);
        // The pointer NuGet puts into LicenseUrl once the license is an expression or a file.
        if (licenseUrl is not null && licenseUrl.Contains("deprecateLicenseUrl", StringComparison.OrdinalIgnoreCase))
            licenseUrl = null;
        if (metadata.LicenseMetadata is { } licenseMetadata)
        {
            license = licenseMetadata.Type switch
            {
                LicenseType.Expression => Clean(licenseMetadata.License, 200),
                LicenseType.File => "License file included in the package",
                _ => null,
            };
        }

        return new PackageDetails(groups, license, licenseUrl, WebAddress(metadata.ProjectUrl?.AbsoluteUri));
    }

    // ">= 1.0.0" or ">= 1.0.0 && < 2.0.0"; empty for "any version", which says nothing.
    private static string ReadableRange(VersionRange? range)
    {
        if (range is null || (range.IsMinInclusive && !range.HasUpperBound && range.MinVersion is { } min && min == new NuGetVersion(0, 0, 0)))
            return "";
        var text = range.PrettyPrint();
        if (text.StartsWith('(') && text.EndsWith(')')) text = text[1..^1];
        return Clean(text, 64) ?? "";
    }

    private static string? Clean(string? text, int maxLength)
    {
        if (text is null) return null;
        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length > maxLength ? cleaned[..maxLength] + "…" : cleaned;
    }

    private static string? WebAddress(string? value)
    {
        if (value is null || value.Length > 2048) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0
            ? uri.AbsoluteUri
            : null;
    }

    // One misconfigured/unreachable source (e.g. an offline corporate feed) shouldn't fail the whole
    // request — the other configured sources still get a chance. Only when every source fails is it
    // an error, and the message deliberately names no source URL (it could carry credentials).
    private static async Task<List<TResult>> QueryAllSourcesAsync<TResult>(
        IReadOnlyList<SourceRepository> repositories, Func<SourceRepository, Task<TResult>> query, CancellationToken ct)
    {
        var outcomes = await Task.WhenAll(repositories.Select(async repository =>
        {
            try
            {
                return (Ok: true, Result: await query(repository));
            }
            catch
            {
                return (Ok: false, Result: default(TResult)!);
            }
        }));

        ct.ThrowIfCancellationRequested();

        if (outcomes.Length > 0 && outcomes.All(o => !o.Ok))
            throw new InvalidOperationException(outcomes.Length == 1
                ? "The package source could not be reached."
                : $"None of the {outcomes.Length} package sources could be reached.");

        return outcomes.Where(o => o.Ok).Select(o => o.Result).ToList();
    }
}
