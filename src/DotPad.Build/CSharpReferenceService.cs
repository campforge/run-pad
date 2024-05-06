using System.Reflection;
using DotPad.Runtime;

namespace DotPad.Build;

// singleton
public sealed class CSharpReferenceService
{
    public CSharpReferenceService() { }

    public string GetExtensionReference()
    {
        return typeof(ObjectExtensions).GetTypeInfo().Assembly.Location;
    }

    public string[] GetDefault()
    {
        var refPaths = new[] {
            typeof(object).GetTypeInfo().Assembly.Location,
            typeof(Console).GetTypeInfo().Assembly.Location,
            Path.Combine(Path.GetDirectoryName(typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly.Location), "System.Runtime.dll")
        };
        return refPaths;
    }
}