using System.Reflection;

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

        foreach (string dll in Directory.GetFiles(runtimeDir, "System.*.dll"))
            paths.Add(dll);

        return [.. paths.Where(File.Exists)];
    }
}
