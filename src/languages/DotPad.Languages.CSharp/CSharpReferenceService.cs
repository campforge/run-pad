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
        return new[]
        {
            typeof(object).GetTypeInfo().Assembly.Location,
            typeof(Console).GetTypeInfo().Assembly.Location,
            Path.Combine(
                Path.GetDirectoryName(typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly.Location)!,
                "System.Runtime.dll"),
        };
    }
}
