using Invoklet.Contracts;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// The Invoklet.Runtime assembly shipped next to Invoklet itself. C# and Visual Basic scripts reference
/// the same one, so it belongs to the shared .NET backend rather than to a language.
/// </summary>
public sealed class DotnetRuntimeReferenceLocator : IRuntimeReferenceLocator
{
    public static string RuntimeAssemblyPath => typeof(ObjectExtensions).Assembly.Location;

    public string GetRuntimeAssemblyPath() => RuntimeAssemblyPath;
}
