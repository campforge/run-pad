using Invoklet.Services.Examples;

namespace Invoklet.Services.QuickOpen;

/// <summary>What Quick Open can open: the scripts under the scripts folder and the recent ones wherever they are, the bundled samples,
/// and the files of other types in the scripts folder.</summary>
public static class QuickOpenCatalog
{
    private const string ScriptExtension = ".inv";

    /// <param name="tree">The scripts folder as the file tree shows it.</param>
    /// <param name="root">The scripts folder.</param>
    /// <param name="recentPaths">Full paths, the file opened last first.</param>
    /// <param name="exists">Whether a recent file is still there (a recent file may have been deleted since).</param>
    /// <param name="comparer">How paths are compared on this system.</param>
    /// <param name="lastWrite">When a file was last written; null when that is not known.</param>
    public static IReadOnlyList<QuickOpenEntry> Scripts(
        IEnumerable<FileSystemItem> tree, string root, IReadOnlyList<string> recentPaths, Func<string, bool> exists,
        StringComparer comparer, Func<string, DateTime?> lastWrite)
    {
        var rank = new Dictionary<string, int>(comparer);
        for (var i = 0; i < recentPaths.Count; i++) rank.TryAdd(recentPaths[i], i);

        var entries = new Dictionary<string, QuickOpenEntry>(comparer);
        foreach (var path in Files(tree).Where(IsScript))
            entries[path] = Create(QuickOpenKind.Script, path, root, rank.GetValueOrDefault(path, -1), lastWrite);
        foreach (var (path, position) in rank)
            if (!entries.ContainsKey(path) && IsScript(path) && exists(path))
                entries[path] = Create(QuickOpenKind.Script, path, root, position, lastWrite);
        return [.. entries.Values];
    }

    /// <summary>The bundled samples: their title for the name and their folder as the Samples tree titles it.</summary>
    public static IReadOnlyList<QuickOpenEntry> Samples(IEnumerable<ExampleScript> examples)
        => [.. examples.Select(e => new QuickOpenEntry(QuickOpenKind.Sample, e.Id, e.Title, ExampleScript.FolderTitle(e.Folder), -1))];

    /// <summary>The files that are not scripts, from the paths found under the scripts folder.</summary>
    public static IReadOnlyList<QuickOpenEntry> OtherFiles(IEnumerable<string> paths, string root, Func<string, DateTime?> lastWrite)
        => [.. paths.Where(p => !IsScript(p)).Select(p => Create(QuickOpenKind.OtherFile, p, root, -1, lastWrite))];

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

    private static QuickOpenEntry Create(QuickOpenKind kind, string path, string root, int recentRank, Func<string, DateTime?> lastWrite)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var relative = Path.GetRelativePath(root, folder);
        // Outside the scripts folder the relative path would climb out of it with "..": the full folder says more.
        var shown = relative == "." ? "" : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? folder : relative;
        // A file of another type keeps its extension in its name: "notes" and "notes.md" are different files.
        var name = kind == QuickOpenKind.OtherFile ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        return new(kind, path, name, shown, recentRank, lastWrite(path));
    }
}
