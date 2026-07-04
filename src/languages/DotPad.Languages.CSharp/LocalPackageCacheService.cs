using DotPad.Contracts;
using NuGet.Configuration;

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

            // BUG-3: строковая сортировка версий даёт неверный результат.
            // "1.10.0" < "1.9.0" при OrdinalIgnoreCase — неверно для SemVer.
            // Правильно: NuGetVersion.Parse(v) + VersionComparer.Default.
            var latestVersion = Directory.EnumerateDirectories(packageDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";

            result.Add(new CachedPackageInfo(id, latestVersion));
        }

        return result.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
