namespace Invoklet.Services.QuickOpen;

/// <summary>Which scripts Quick Open offers: those under the scripts folder and the recent ones wherever they are.</summary>
public static class QuickOpenCatalog
{
    private const string ScriptExtension = ".inv";

    /// <param name="tree">The scripts folder as the file tree shows it.</param>
    /// <param name="root">The scripts folder.</param>
    /// <param name="recentPaths">Full paths, the file opened last first.</param>
    /// <param name="exists">Whether a recent file is still there (a recent file may have been deleted since).</param>
    /// <param name="comparer">How paths are compared on this system.</param>
    public static IReadOnlyList<QuickOpenEntry> Build(
        IEnumerable<FileSystemItem> tree, string root, IReadOnlyList<string> recentPaths, Func<string, bool> exists, StringComparer comparer)
    {
        var rank = new Dictionary<string, int>(comparer);
        for (var i = 0; i < recentPaths.Count; i++) rank.TryAdd(recentPaths[i], i);

        var entries = new Dictionary<string, QuickOpenEntry>(comparer);
        foreach (var path in Files(tree).Where(IsScript))
            entries[path] = Create(path, root, rank.GetValueOrDefault(path, -1));
        foreach (var (path, position) in rank)
            if (!entries.ContainsKey(path) && IsScript(path) && exists(path))
                entries[path] = Create(path, root, position);
        return [.. entries.Values];
    }

    private static IEnumerable<string> Files(IEnumerable<FileSystemItem> items)
    {
        foreach (var item in items)
        {
            if (!item.IsDirectory) yield return item.FilePath;
            else if (item.Children is { } children)
                foreach (var path in Files(children)) yield return path;
        }
    }

    private static bool IsScript(string path) => string.Equals(Path.GetExtension(path), ScriptExtension, StringComparison.OrdinalIgnoreCase);

    private static QuickOpenEntry Create(string path, string root, int recentRank)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var relative = Path.GetRelativePath(root, folder);
        // Outside the scripts folder the relative path would climb out of it with "..": the full folder says more.
        var shown = relative == "." ? "" : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? folder : relative;
        return new(path, Path.GetFileNameWithoutExtension(path), shown, recentRank);
    }
}
