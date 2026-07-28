namespace RunPad.Contracts;

public record CachedPackageInfo(string Id, string LatestVersion);

public interface ILocalPackageCacheService
{
    IReadOnlyList<CachedPackageInfo> GetCachedPackages();
}
