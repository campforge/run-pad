namespace Invoklet.Services;

/// <summary>
/// The paths of the file tree operations (rename, move, new file or folder, and the tabs they touch), worked out
/// without touching the disk: whether a path is a folder is told by the caller, which has just asked the disk.
/// </summary>
internal sealed class FileTreePaths(string rootFolder, StringComparison comparison)
{
    /// <summary>
    /// The folder a new node or a moved one goes into: the tree's root when nothing is selected, inside a selected
    /// folder, alongside a selected file.
    /// </summary>
    public string ParentFolder(string? contextPath, bool contextIsFolder)
        => contextPath is null
            ? rootFolder
            : contextIsFolder
                ? contextPath
                : Path.GetDirectoryName(contextPath) ?? rootFolder;

    /// <summary>The path after renaming; a file keeps its extension, a folder has none to keep.</summary>
    public static string Renamed(string path, string newName, bool isFolder)
        => Path.Combine(Path.GetDirectoryName(path)!, newName + (isFolder ? "" : Path.GetExtension(path)));

    /// <summary>The path after moving into <paramref name="targetFolder"/> under the same name.</summary>
    public static string Moved(string path, string targetFolder)
        => Path.Combine(targetFolder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

    /// <summary>Dropping a node where it already is: not an error, nothing to do.</summary>
    public static bool IsSamePlace(string path, string other)
        => string.Equals(Path.GetFullPath(path), Path.GetFullPath(other), StringComparison.Ordinal);

    /// <summary>The open paths affected by an operation on <paramref name="path"/>: itself and, for a folder, everything below it.</summary>
    public List<string> AffectedPaths(IEnumerable<string> openPaths, string path, bool isFolder)
    {
        var prefix = path + Path.DirectorySeparatorChar;
        return [.. openPaths.Where(p => string.Equals(p, path, comparison) || (isFolder && p.StartsWith(prefix, comparison)))];
    }

    /// <summary>Where a tab that was open on <paramref name="oldPath"/> is opened again after <paramref name="source"/> became <paramref name="destination"/>.</summary>
    public static string Reopened(string source, string destination, string oldPath, bool isFolder)
        => isFolder ? Path.Combine(destination, Path.GetRelativePath(source, oldPath)) : destination;
}
