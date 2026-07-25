using DotPad.Build.Core;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using NuGet.Versioning;

namespace DotPad.Languages.CSharp.Sdk;

public sealed class DotnetSdkProvider : ISdkProvider
{
    // .NET 5 and earlier are out of Microsoft support
    // (https://dotnet.microsoft.com/platform/support/policy/dotnet-core) and predate this project's
    // own minimum-supported language surface — not worth offering as a script target even if
    // somehow still installed alongside newer SDKs (ANALYSIS.md #94).
    private const int MinSupportedSdkMajorVersion = 6;

    public IReadOnlyList<DotnetSdk> Get()
    {
        List<(string tfm, NuGetVersion version)> versions = [];

        foreach (string line in Utils.Shell("dotnet --list-sdks"))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 0) continue;

            string versionStr = trimmed[..spaceIdx];
            if (!NuGetVersion.TryParse(versionStr, out NuGetVersion? v) || v.Major < MinSupportedSdkMajorVersion) continue;

            versions.Add(($"net{v.Major}.{v.Minor}", v));
        }

        return versions
            .GroupBy(x => x.tfm)
            .Select(g => g.MaxBy(x => x.version))
            .OrderBy(x => x.version.IsPrerelease)
            .ThenByDescending(x => x.version)
            .Select(x => new DotnetSdk(".NET", x.tfm, $"{x.version.Major}.{x.version.Minor}.0", $".NET {x.version}"))
            .ToList();
    }
}
