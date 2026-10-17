using RunPad.Contracts;

namespace RunPad.Languages.DotnetCommon;

/// <summary>
/// The RunPad.Runtime assembly shipped next to RunPad itself. C# and Visual Basic scripts reference
/// the same one, so it belongs to the shared .NET backend rather than to a language.
/// </summary>
public sealed class DotnetRuntimeReferenceLocator : IRuntimeReferenceLocator
{
    public static string RuntimeAssemblyPath => typeof(ObjectExtensions).Assembly.Location;

    public string GetRuntimeAssemblyPath() => RuntimeAssemblyPath;
}
