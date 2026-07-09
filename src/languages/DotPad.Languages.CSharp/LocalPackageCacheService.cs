using DotPad.Contracts;
using NuGet.Configuration;
using NuGet.Versioning;

namespace DotPad.Languages.CSharp;

public sealed class LocalPackageCacheService : ILocalPackageCacheService
{
    public IReadOnlyList<CachedPackageInfo> GetCachedPackages()
    {
        string folder;
        try
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            folder = SettingsUtility.GetGlobalPackagesFolder(settings);
        }
        catch
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        }

        if (!Directory.Exists(folder))
            return [];

        var result = new List<CachedPackageInfo>();
        foreach (var packageDir in Directory.EnumerateDirectories(folder))
        {
            var id = Path.GetFileName(packageDir);
            if (string.IsNullOrEmpty(id)) continue;

            var latestVersion = Directory.EnumerateDirectories(packageDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderByDescending(v => NuGetVersion.TryParse(v, out var nv) ? nv : new NuGetVersion(0, 0, 0))
                .FirstOrDefault() ?? "";

            result.Add(new CachedPackageInfo(id, latestVersion));
        }

        return result.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
