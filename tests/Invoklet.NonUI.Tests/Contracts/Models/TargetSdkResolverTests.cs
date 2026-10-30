using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Which installed SDK runs a script: the one its targetSdk names (the same rule in the window and
// the command-line runner). The providers list the newest stable SDK first.
public class TargetSdkResolverTests
{
    private static readonly DotnetSdk Net10 = new(".NET", "net10.0", "10.0.0", ".NET 10.0.100");
    private static readonly DotnetSdk Net8 = new(".NET", "net8.0", "8.0.0", ".NET 8.0.400");
    private static readonly IReadOnlyList<DotnetSdk> Installed = [Net10, Net8];

    [Fact]
    public void Latest_Should_BeTheNewestSdk()
    {
        Assert.Same(Net10, TargetSdkResolver.Resolve(Installed, TargetSdk.Default));
        Assert.Null(TargetSdkResolver.Warning(TargetSdk.Default, Net10));
    }

    [Fact]
    public void AnInstalledVersion_Should_BeThatSdk()
    {
        var target = TargetSdk.Parse("net8.0");

        Assert.Same(Net8, TargetSdkResolver.Resolve(Installed, target));
        Assert.Null(TargetSdkResolver.Warning(target, Net8));
    }

    [Fact]
    public void AMissingVersion_Should_RunOnTheNewestSdk_WithAWarning()
    {
        var target = TargetSdk.Parse("net6.0");

        var sdk = TargetSdkResolver.Resolve(Installed, target);

        Assert.Same(Net10, sdk);
        Assert.Equal("targetSdk net6.0 is not installed — running on .NET 10.0.100.", TargetSdkResolver.Warning(target, sdk!));
    }

    [Fact]
    public void NoSdks_Should_ResolveToNull()
    {
        Assert.Null(TargetSdkResolver.Resolve([], TargetSdk.Default));
        Assert.Null(TargetSdkResolver.LatestChoice([]));
    }

    [Fact]
    public void TheLatestChoice_Should_RunOnTheNewestSdk_AndSaySo()
    {
        var latest = TargetSdkResolver.LatestChoice(Installed)!;

        Assert.Equal("latest (.NET 10.0.100)", latest.Description);
        Assert.Equal("net10.0", latest.TargetFrameworkMoniker);
    }

    [Fact]
    public void ToTarget_Should_BeTheSdksVersion()
        => Assert.Equal(TargetSdk.Parse("net8.0"), TargetSdkResolver.ToTarget(Net8));
}
