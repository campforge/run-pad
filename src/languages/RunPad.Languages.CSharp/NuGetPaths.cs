using NuGet.Configuration;
using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.CSharp;

internal static class NuGetPaths
{
    // Shared by LocalPackageCacheService/CSharpIntelliSenseService — both used to duplicate this
    // exact try/catch.
    public static string GetGlobalPackagesFolder()
    {
        try
        {
            // Scripts restore with RunPad's own NuGet.Config once it exists (RunPadNuGetConfigStore),
            // so packages land in ITS globalPackagesFolder — follow it, but never create it here.
            var settings = RunPadNuGetConfigStore.ExistingDefaultConfigFile is { } runPadConfig
                ? Settings.LoadSpecificSettings(Path.GetDirectoryName(runPadConfig)!, Path.GetFileName(runPadConfig))
                : Settings.LoadDefaultSettings(root: null);
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
