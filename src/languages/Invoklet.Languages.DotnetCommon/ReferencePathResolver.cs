using Invoklet.Format.MdRun;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// A script's <c>references:</c> entries as the build, the CLI and IntelliSense all see them.
/// </summary>
/// <param name="Assemblies">Full paths of the .dll references that exist.</param>
/// <param name="Projects">.csproj/.vbproj references, macros expanded — reported as unsupported by the build.</param>
/// <param name="Errors">References that can't be used at all; the build fails with them.</param>
/// <param name="Warnings">References that resolved to a file that isn't there.</param>
public sealed record ResolvedReferences(
    IReadOnlyList<string> Assemblies, IReadOnlyList<string> Projects, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static ResolvedReferences None { get; } = new([], [], [], []);
}

/// <summary>
/// Turns <c>references:</c> entries into files. Macros are expanded here, never in the stored text.
/// A relative path is relative to the folder of the script itself — never the process's current
/// directory — so the GUI, the CLI and IntelliSense find the same file. An untitled script has no
/// folder yet: its relative references (and <c>$(CurrentQueryPath)</c>) are reported, not guessed.
/// </summary>
public static class ReferencePathResolver
{
    public static ResolvedReferences Resolve(IEnumerable<string>? references, string? scriptPath) =>
        Resolve(references, scriptPath, OperatingSystem.IsWindows());

    internal static ResolvedReferences Resolve(IEnumerable<string>? references, string? scriptPath, bool windowsHost)
    {
        if (references is null) return ResolvedReferences.None;

        var scriptFullPath = scriptPath is null ? null : Path.GetFullPath(scriptPath);
        List<string> assemblies = [], projects = [], errors = [], warnings = [];
        foreach (var reference in references)
        {
            if (reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || reference.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(ReferencePathMacros.Expand(reference, scriptFullPath));
                continue;
            }
            if (!reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var (path, error) = ResolveOne(reference, scriptFullPath, windowsHost);
            if (error is not null)
                errors.Add(error);
            else if (!File.Exists(path))
                warnings.Add(NotFound(reference, path!, windowsHost));
            else
                assemblies.Add(path!);
        }
        return new ResolvedReferences(assemblies, projects, errors, warnings);
    }

    internal static (string? Path, string? Error) ResolveOne(string reference, string? scriptFullPath, bool windowsHost)
    {
        if (scriptFullPath is null && reference.Contains(ReferencePath.CurrentQueryPathMacro, StringComparison.Ordinal))
            return (null, $"Reference '{reference}' uses $(CurrentQueryPath), the script's folder: save the script first.");

        var expanded = ReferencePathMacros.Expand(reference, scriptFullPath);
        switch (ReferencePath.Classify(expanded))
        {
            case ReferencePathKind.WindowsDriveRelative:
                return (null, $"Reference '{reference}' is relative to the current folder of a Windows drive, which Invoklet can't know. " +
                              "Write it as an absolute path (for example 'C:/Libraries/Library.dll') or relative to the script.");
            case ReferencePathKind.WindowsDrive or ReferencePathKind.WindowsUnc when !windowsHost:
                return (null, $"Reference '{reference}' is a Windows path and can't be used on this system. " +
                              "Use a path relative to the script, or one that exists here.");
            case ReferencePathKind.Relative:
                if (scriptFullPath is null)
                    return (null, $"Reference '{reference}' is relative to the script's folder: save the script first, or use an absolute path.");
                return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptFullPath)!, expanded)), null);
            default:
                return (Path.GetFullPath(expanded), null);
        }
    }

    private static string NotFound(string reference, string path, bool windowsHost) =>
        !windowsHost && reference.Contains('\\')
            ? $"Reference '{reference}' was not found at '{path}'. On this system '\\' is part of a file name, not a folder separator: write '/' instead."
            : $"Reference '{reference}' was not found at '{path}'.";
}
