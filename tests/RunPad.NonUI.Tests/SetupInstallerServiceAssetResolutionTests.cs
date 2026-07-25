using System.Runtime.InteropServices;
using RunPad.Services.Setup;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Pure mapping logic (OS/arch -> asset name/URL, OS -> dotnet-install default install dir) taken as
// parameters specifically so every branch is testable regardless of which OS actually runs this suite.
public class SetupInstallerServiceAssetResolutionTests
{
    [Theory]
    [InlineData(SetupOSPlatform.Windows, Architecture.X64, "netcoredbg-win64.zip",
        "https://github.com/Samsung/netcoredbg/releases/latest/download/netcoredbg-win64.zip")]
    [InlineData(SetupOSPlatform.Windows, Architecture.Arm64, "netcoredbg-win64.zip",
        "https://github.com/Samsung/netcoredbg/releases/latest/download/netcoredbg-win64.zip")]
    [InlineData(SetupOSPlatform.Linux, Architecture.X64, "netcoredbg-linux-amd64.tar.gz",
        "https://github.com/Samsung/netcoredbg/releases/latest/download/netcoredbg-linux-amd64.tar.gz")]
    [InlineData(SetupOSPlatform.Linux, Architecture.Arm64, "netcoredbg-linux-arm64.tar.gz",
        "https://github.com/Samsung/netcoredbg/releases/latest/download/netcoredbg-linux-arm64.tar.gz")]
    [InlineData(SetupOSPlatform.MacOS, Architecture.Arm64, "netcoredbg-osx-arm64.zip",
        "https://github.com/Samsung/netcoredbg/releases/latest/download/netcoredbg-osx-arm64.zip")]
    public void ResolveNetcoredbgAsset_Should_PickTheLatestRelease_ForEveryPlatformExceptIntelMac(
        SetupOSPlatform platform, Architecture arch, string expectedAssetName, string expectedUrl)
    {
        var (assetName, url) = SetupInstallerService.ResolveNetcoredbgAsset(platform, arch);

        Assert.Equal(expectedAssetName, assetName);
        Assert.Equal(expectedUrl, url.ToString());
    }

    // netcoredbg's newest release (3.2.0) dropped the osx-amd64 build every release back to 3.1.0
    // shipped — Intel Mac must pin to the last release known to still have one, not "latest".
    [Fact]
    public void ResolveNetcoredbgAsset_Should_PinAnOlderRelease_ForIntelMac()
    {
        var (assetName, url) = SetupInstallerService.ResolveNetcoredbgAsset(SetupOSPlatform.MacOS, Architecture.X64);

        Assert.Equal("netcoredbg-osx-amd64.tar.gz", assetName);
        Assert.Equal(
            "https://github.com/Samsung/netcoredbg/releases/download/3.1.3-1062/netcoredbg-osx-amd64.tar.gz",
            url.ToString());
    }

    [Fact]
    public void ResolveDotnetInstallDir_Should_UseLocalAppDataMicrosoftDotnet_OnWindows()
    {
        var dir = SetupInstallerService.ResolveDotnetInstallDir(SetupOSPlatform.Windows);

        Assert.EndsWith(Path.Combine("Microsoft", "dotnet"), dir);
    }

    [Theory]
    [InlineData(SetupOSPlatform.Linux)]
    [InlineData(SetupOSPlatform.MacOS)]
    public void ResolveDotnetInstallDir_Should_UseHomeDotDotnet_OnLinuxAndMacOS(SetupOSPlatform platform)
    {
        var dir = SetupInstallerService.ResolveDotnetInstallDir(platform);

        Assert.EndsWith(".dotnet", dir);
        Assert.DoesNotContain("Microsoft", dir);
    }
}
