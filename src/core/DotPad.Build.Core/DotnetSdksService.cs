using DotPad.Build.Core.Models;
using DotPad.Build.Core.Sdk;
using NuGet.Versioning;

namespace DotPad.Build.Core;

public sealed class DotnetSdksService
{
    private readonly Lazy<ListSdks> _listSdks = new(() => new ListSdks());

    public IReadOnlyList<ExecutionPlatform> GetSdkInfos()
    {
        var sdks = _listSdks.Value.ToList();

        if (!sdks.Any())
            return Array.Empty<ExecutionPlatform>();

        var versions = new List<(string name, string tfm, NuGetVersion version)>();
        foreach (var sdk in sdks)
        {
            if (NuGetVersion.TryParse(sdk.Name, out var version) && version.Major > 5)
            {
                var tfm = $"net{version.Major}.{version.Minor}";
                versions.Add((".NET", tfm, version));
            }
        }

        return versions
            .GroupBy(c => c.tfm)
            .Select(c => c.MaxBy(i => i.version))
            .OrderBy(c => c.version.IsPrerelease)
            .ThenByDescending(c => c.version)
            .Select(v => new ExecutionPlatform(v.name, v.tfm, v.version))
            .ToList();
    }
}
