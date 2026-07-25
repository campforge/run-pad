using NuGet.Configuration;

namespace RunPad.Languages.CSharp;

internal static class NuGetPaths
{
    // Shared by LocalPackageCacheService/CSharpIntelliSenseService — both used to duplicate this
    // exact try/catch (see TODO.md #47).
    public static string GetGlobalPackagesFolder()
    {
        try
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            return SettingsUtility.GetGlobalPackagesFolder(settings);
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        }
    }
}
