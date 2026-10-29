using Invoklet.Format.RPad;

namespace Invoklet.Contracts.Models;

/// <summary>
/// Which installed SDK runs a script: the one its front matter's <c>targetSdk</c> names — the same
/// rule for the Invoklet window and the command-line runner. <c>latest</c> (or no version) means the
/// newest stable SDK, the first of <see cref="IReadOnlyList{T}"/> as the SDK providers order them.
/// </summary>
public static class TargetSdkResolver
{
    /// <summary>The SDK for <paramref name="target"/>; the newest one when the version it names isn't
    /// installed (see <see cref="Warning"/>). Null only when no SDK is installed.</summary>
    public static DotnetSdk? Resolve(IReadOnlyList<DotnetSdk> available, TargetSdk target)
        => IsLatest(target)
            ? available.FirstOrDefault()
            : available.FirstOrDefault(s => Matches(s, target)) ?? available.FirstOrDefault();

    /// <summary>Why <paramref name="sdk"/> isn't what <paramref name="target"/> asks for, or null.</summary>
    public static string? Warning(TargetSdk target, DotnetSdk sdk)
        => IsLatest(target) || Matches(sdk, target)
            ? null
            : $"targetSdk {target} is not installed — running on {sdk.Description}.";

    /// <summary>The toolbar's "latest" choice: runs on the newest SDK, shown as
    /// "latest (.NET 10.0.100)"; picking it stores <c>targetSdk: latest</c>. Null without SDKs.</summary>
    public static DotnetSdk? LatestChoice(IReadOnlyList<DotnetSdk> available)
        => available.FirstOrDefault() is { } newest
            ? new DotnetSdk(newest.Name, newest.TargetFrameworkMoniker, newest.RuntimeConfigVersion, $"latest ({newest.Description})")
            : null;

    /// <summary>The <c>targetSdk</c> value that selects <paramref name="sdk"/>.</summary>
    public static TargetSdk ToTarget(DotnetSdk sdk) => TargetSdk.Parse(sdk.TargetFrameworkMoniker);

    public static bool IsLatest(TargetSdk target)
        => target.Version is null || string.Equals(target.Name, "latest", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(DotnetSdk sdk, TargetSdk target)
        => string.Equals(sdk.TargetFrameworkMoniker, target.ToString(), StringComparison.OrdinalIgnoreCase);
}
