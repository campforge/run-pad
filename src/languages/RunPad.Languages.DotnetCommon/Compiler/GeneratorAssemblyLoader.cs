using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace RunPad.Languages.DotnetCommon.Compiler;

// Loads source generators (targeting pack and NuGet packages) for in-process compilation. One AssemblyLoadContext per
// generator directory: the net8/net9/net10 packs ship generator assemblies with the SAME names
// (System.Text.Json.SourceGeneration, ...), which can't share one context. Anything not found in the
// directory — above all Microsoft.CodeAnalysis itself — falls back to RunPad's own (default)
// context, so generators run against the Roslyn RunPad already has loaded.
public sealed class GeneratorAssemblyLoader : IAnalyzerAssemblyLoader
{
    public static GeneratorAssemblyLoader Instance { get; } = new();

    private readonly ConcurrentDictionary<string, DirectoryContext> _contexts =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private GeneratorAssemblyLoader()
    {
    }

    public void AddDependencyLocation(string fullPath)
    {
        // Dependencies are resolved from the generator's own directory (see DirectoryContext.Load).
    }

    public Assembly LoadFromPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        return _contexts.GetOrAdd(directory, dir => new DirectoryContext(dir)).LoadFromAssemblyPath(fullPath);
    }

    private sealed class DirectoryContext(string directory)
        : AssemblyLoadContext($"RunPad generators: {directory}", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
