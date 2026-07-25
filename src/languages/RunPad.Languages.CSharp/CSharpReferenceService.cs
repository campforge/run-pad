using System.Reflection;
using System.Reflection.PortableExecutable;

namespace DotPad.Languages.CSharp;

public sealed class CSharpReferenceService
{
    public string GetExtensionReference()
    {
        return typeof(ObjectExtensions).GetTypeInfo().Assembly.Location;
    }

    public string[] GetDefault()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string? refDir = FindNetCoreAppRefDirectory(runtimeDir);

        if (refDir is not null)
        {
            // Prefer the SDK's own reference-assembly pack: metadata-only facades (System.Runtime.dll,
            // System.Console.dll, ...) that — unlike the runtime DLLs in the fallback branch below —
            // ship with a matching .xml doc file each, which is what lets ReferenceDocumentation
            // populate completion/signature-help Summary for BCL symbols (see ANALYSIS.md TODO #50).
            // Mixing these with the runtime's own implementation DLLs would fail with CS0433
            // ("type exists in both...") since both define System.Object et al., so this replaces the
            // runtime-dir scan below entirely rather than adding to it.
            return [.. Directory.GetFiles(refDir, "*.dll").Where(IsManagedAssembly)];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(object).Assembly.Location,
            typeof(Console).Assembly.Location,
            Path.Combine(runtimeDir, "System.Runtime.dll"),
            // netstandard2.0 assemblies (e.g. DotPad.Runtime, which defines .Dump()) type-forward
            // most BCL types through netstandard.dll — without it Roslyn can't fully resolve such
            // an assembly's public surface, so anything referencing it shows spurious CS0012 errors.
            Path.Combine(runtimeDir, "netstandard.dll"),
        };

        // The "System.*.dll" mask also matches native companion libraries that ship next to the
        // managed BCL assemblies on some platforms (e.g. Windows' shared/Microsoft.NETCore.App/
        // <version>/System.IO.Compression.Native.dll) — MetadataReference.CreateFromFile on one of
        // those throws CS0009 ("no managed metadata"). Filter by actually reading the PE header
        // instead of trusting the filename.
        foreach (string dll in Directory.GetFiles(runtimeDir, "System.*.dll"))
            if (IsManagedAssembly(dll))
                paths.Add(dll);

        return [.. paths.Where(File.Exists)];
    }

    // The reference-assembly pack ships alongside every dotnet SDK install (not just the runtime),
    // at <installRoot>/packs/Microsoft.NETCore.App.Ref/<version>/ref/netX.0/ — a sibling of
    // <installRoot>/shared/Microsoft.NETCore.App/<runtimeVersion>, which is what runtimeDir here is.
    private static string? FindNetCoreAppRefDirectory(string runtimeDir)
    {
        try
        {
            string? installRoot = Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
            string majorVersion = Path.GetFileName(runtimeDir).Split('.')[0];
            if (installRoot is null || !int.TryParse(majorVersion, out _)) return null;

            string refPacksRoot = Path.Combine(installRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(refPacksRoot)) return null;

            // .NET's mainline TFMs are always netX.0 regardless of the runtime's own minor/patch.
            string tfm = $"net{majorVersion}.0";
            return Directory.GetDirectories(refPacksRoot)
                .Select(d => new { Dir = d, Ver = Version.TryParse(Path.GetFileName(d), out var v) ? v : null })

                .Where(x => x.Ver is not null)

                .OrderByDescending(x => x.Ver)

                .Select(x => Path.Combine(x.Dir, "ref", tfm))

                .FirstOrDefault(Directory.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
}
