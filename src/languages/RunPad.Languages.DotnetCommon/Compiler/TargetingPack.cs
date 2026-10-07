using System.Reflection.PortableExecutable;

namespace RunPad.Languages.DotnetCommon.Compiler;

// The Microsoft.NETCore.App.Ref targeting pack for one target framework — what a script compiles
// against: the TARGET's reference assemblies (not RunPad's own runtime) and the source generators that
// ship with them ([GeneratedRegex], System.Text.Json, [LibraryImport]).
public sealed record TargetingPack(string Version, string PackDirectory, string ReferenceDirectory)
{
    public const string PackageId = "Microsoft.NETCore.App.Ref";

    // Highest version across the packs installed with the SDKs (<dotnet>/packs/...) and the ones the
    // SDK downloaded into the NuGet cache (`dotnet build` itself compiles against the latest patch from
    // NuGet, e.g. 8.0.22 while the installed pack is 8.0.2). Null when neither has the target.
    public static TargetingPack? Find(string targetFramework, string? dotnetExecutable, string? globalPackagesFolder)
    {
        if (!TargetFramework.TryParse(targetFramework, out var major, out var minor)) return null;

        var roots = DotnetRoots(dotnetExecutable).Select(r => Path.Combine(r, "packs", PackageId)).ToList();
        if (!string.IsNullOrEmpty(globalPackagesFolder))
            roots.Add(Path.Combine(globalPackagesFolder, PackageId.ToLowerInvariant()));

        return roots
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(dir => (Dir: dir, Version: System.Version.TryParse(Path.GetFileName(dir), out var v) ? v : null))
            .Where(p => p.Version is { } v && v.Major == major && v.Minor == minor)
            .Select(p => (p.Dir, p.Version, Ref: Path.Combine(p.Dir, "ref", targetFramework)))
            .Where(p => Directory.Exists(p.Ref))
            .OrderByDescending(p => p.Version)
            .Select(p => new TargetingPack(p.Version!.ToString(), p.Dir, p.Ref))
            .FirstOrDefault();
    }

    public IEnumerable<string> ReferenceAssemblies =>
        Directory.GetFiles(ReferenceDirectory, "*.dll").Where(IsManagedAssembly);

    // analyzers/dotnet/cs or analyzers/dotnet/vb — the generators for one language.
    public IEnumerable<string> GeneratorAssemblies(string languageFolder)
    {
        var dir = Path.Combine(PackDirectory, "analyzers", "dotnet", languageFolder);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dll").Where(IsManagedAssembly) : [];
    }

    internal static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            return peReader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    // The dotnet the script runs with (a configured full path, or "dotnet" on PATH), DOTNET_ROOT, and
    // the install RunPad itself runs on — the SDK that `dotnet build` would have used comes first.
    private static IEnumerable<string> DotnetRoots(string? dotnetExecutable)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var candidate in Candidates(dotnetExecutable))
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (Directory.Exists(full) && seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string?> Candidates(string? dotnetExecutable)
    {
        if (!string.IsNullOrWhiteSpace(dotnetExecutable))
        {
            if (Path.IsPathRooted(dotnetExecutable))
                yield return Path.GetDirectoryName(ResolveLink(dotnetExecutable));
            else
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var exe = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? dotnetExecutable + ".exe" : dotnetExecutable);
                    if (File.Exists(exe)) yield return Path.GetDirectoryName(ResolveLink(exe));
                }
        }

        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT");

        // <root>/shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        yield return runtimeDir is null ? null : Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
    }

    // /usr/local/bin/dotnet is usually a symlink into the real install root (macOS/Linux packages).
    private static string ResolveLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}

// "net8.0" -> 8, 0. Script targets are always netX.Y with X >= 5.
public static class TargetFramework
{
    public static bool TryParse(string tfm, out int major, out int minor)
    {
        major = minor = 0;
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = tfm[3..].Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor) && major >= 5;
    }
}
