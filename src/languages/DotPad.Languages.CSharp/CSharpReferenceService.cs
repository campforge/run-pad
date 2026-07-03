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
        };

        foreach (string dll in Directory.GetFiles(runtimeDir, "System.*.dll"))
            paths.Add(dll);

        return [.. paths.Where(File.Exists)];
    }
}
