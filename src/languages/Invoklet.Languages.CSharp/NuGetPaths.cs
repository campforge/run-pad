using Invoklet.Languages.DotnetCommon;
using NuGet.Configuration;

namespace Invoklet.Languages.CSharp;

internal static class NuGetPaths
{
    // Shared by LocalPackageCacheService/CSharpIntelliSenseService — both used to duplicate this
    // exact try/catch.
    public static string GetGlobalPackagesFolder()
    {
        try
        {
            // Scripts restore with Invoklet's own NuGet.Config once it exists (InvokletNuGetConfigStore),
            // so packages land in ITS globalPackagesFolder — follow it, but never create it here.
            var settings = InvokletNuGetConfigStore.ExistingDefaultConfigFile is { } InvokletConfig
                ? Settings.LoadSpecificSettings(Path.GetDirectoryName(InvokletConfig)!, Path.GetFileName(InvokletConfig))
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
