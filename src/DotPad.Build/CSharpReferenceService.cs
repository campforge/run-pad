using System.Reflection;

namespace DotPad.Build;

public sealed class CSharpReferenceService
{
    public CSharpReferenceService() { }
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