using DotPad.Build.Models;
using DotPad.Build.Sdk;
using NuGet.Versioning;

namespace DotPad.Build
{
    public sealed class DotnetSdksService
    {
        private readonly Lazy<ListSdks> _listSdks = new(() => new ListSdks());

        public IReadOnlyList<ExecutionPlatform> GetSdkInfos()
        {
            var sdks = _listSdks.Value.ToList();

            if (!sdks.Any())
            {
                return Array.Empty<ExecutionPlatform>();
            }
            var versions = new List<(string name, string tfm, NuGetVersion version)>();
            foreach (var sdk in sdks)
            {
                var versionName = sdk.Name;
                if (NuGetVersion.TryParse(versionName, out var version) && version.Major > 5)
                {
                    var name = ".NET";
                    var tfm = $"net{version.Major}.{version.Minor}";
                    versions.Add((name, tfm, version));
                }
            }

            return versions
               .GroupBy(c => c.tfm)
               .Select(c => c.MaxBy(i => i.version))
               .OrderBy(c => c.version.IsPrerelease)
               .ThenByDescending(c => c.version)
               .Select(version => new ExecutionPlatform(
                   version.name,
                    version.tfm,
                    version.version

                   ))
               .ToList();
        }
    }
}
