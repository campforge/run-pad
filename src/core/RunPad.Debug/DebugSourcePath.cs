namespace RunPad.Debug;

/// <summary>
/// Maps a source path onto the spelling the compiler recorded in the PDB, which is what netcoredbg
/// matches breakpoint sources against. MsBuildProcessCompiler runs <c>dotnet build</c> with the
/// output directory as its working directory, and on Unix the child's <c>getcwd()</c> returns the
/// fully symlink-resolved path — so on macOS a temp directory spelled <c>/var/folders/...</c> (with
/// <c>/var</c> a symlink to <c>/private/var</c>) ends up as <c>/private/var/folders/...</c> in the
/// PDB, and a breakpoint sent for the <c>/var/...</c> alias stays pending forever. Every symlink in
/// the path is resolved, not only the leaf. Windows' current directory doesn't resolve links, so
/// paths there are returned unchanged.
/// </summary>
public static class DebugSourcePath
{
    // Guards against symlink cycles, same order of magnitude as the OS's own ELOOP limit.
    private const int MaxLinkHops = 40;

    public static string Canonicalize(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            return path;

        try
        {
            return Resolve(Path.GetFullPath(path), 0) ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Whether two source paths name the same file once canonicalized.</summary>
    public static bool AreSame(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Canonicalize(a), Canonicalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Resolve(string fullPath, int hops)
    {
        var root = Path.GetPathRoot(fullPath)!;
        var parts = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops) return null;

            // A relative link target is relative to the directory containing the link.
            var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            var rest = parts.Skip(i + 1).ToArray();
            return Resolve(rest.Length == 0 ? resolved : Path.Combine([resolved, .. rest]), hops);
        }

        return current;
    }
}
