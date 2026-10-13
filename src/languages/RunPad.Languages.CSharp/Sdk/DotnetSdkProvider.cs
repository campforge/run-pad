using System.Diagnostics;
using NuGet.Versioning;
using RunPad.Contracts;
using RunPad.Contracts.Models;

namespace RunPad.Languages.CSharp.Sdk;

public sealed class DotnetSdkProvider : ISdkProvider
{
    // .NET 5 and earlier are out of Microsoft support
    // (https://dotnet.microsoft.com/platform/support/policy/dotnet-core) and predate this project's
    // own minimum-supported language surface — not worth offering as a script target even if
    // somehow still installed alongside newer SDKs.
    private const int MinSupportedSdkMajorVersion = 6;

    public IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null)
    {
        var fileName = string.IsNullOrWhiteSpace(dotnetExecutableOverride) ? "dotnet" : dotnetExecutableOverride;
        List<(string tfm, NuGetVersion version)> versions = [];

        foreach (string line in ListSdks(fileName))
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

    // Runs Process directly (not the shared Utils.Shell helper) — a user-configured
    // dotnetExecutableOverride can be a full path containing spaces (e.g. Windows' own default
    // "C:\Program Files\dotnet\dotnet.exe"), and Utils.Shell's SplitArguments naively splits its
    // whole commandText on spaces with no quoting support at all — safe for the literal, space-free
    // "dotnet" command its one existing caller passes, but would silently mangle a real spaced path.
    private static IEnumerable<string> ListSdks(string fileName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--list-sdks");

        using var process = Process.Start(startInfo);
        if (process is null) yield break;

        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
            yield return line;

        process.WaitForExit();
    }
}
