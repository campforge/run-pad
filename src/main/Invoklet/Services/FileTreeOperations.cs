namespace Invoklet.Services;

/// <summary>A tab open on a file: <paramref name="Handle"/> is the tab itself, whatever its file becomes, and <paramref name="Path"/> the file it had when it was listed.</summary>
internal sealed record FileOperationTab(object Handle, string Path);

/// <summary>The open tabs, as the file tree operations see them.</summary>
internal interface IFileOperationTabs
{
    IReadOnlyList<FileOperationTab> OpenTabs { get; }

    /// <summary>The file the tab is on now (a Save As changes it); null when the tab is gone.</summary>
    string? CurrentPath(FileOperationTab tab);

    /// <summary>Saves these tabs. False when one could not be (the user was told) or the user cancelled: the operation must not go on.</summary>
    Task<bool> SaveAsync(IReadOnlyList<FileOperationTab> tabs, string errorTitle);

    void Close(IReadOnlyList<FileOperationTab> tabs);

    void Open(string path);
}

/// <summary>What the file tree operations do to the tree on screen.</summary>
internal interface IFileTreeSurface
{
    void Remove(string path);

    void Refresh();
}

/// <summary>
/// Delete, rename and move of files and folders, and what they do to the open tabs. The tabs of the affected
/// files are saved first (a failed write stops the operation) and closed, or closed and opened at their new
/// paths, only once the disk operation has succeeded: a failure leaves the very same tabs open, with their editor
/// state, instead of closing them and reading them back from disk. A tab that a Save As moved to another file
/// meanwhile is no longer on the file being operated on and is left open as it is.
/// </summary>
internal sealed class FileTreeOperations(
    IDiskOperations disk, IWindowService windows, IFileOperationTabs tabs, IFileTreeSurface tree, FileTreePaths paths)
{
    public async Task DeleteAsync(string path)
    {
        var isFolder = disk.IsDirectory(path);
        var listed = Affected(path, isFolder);
        if (!await tabs.SaveAsync(listed, "Delete Failed")) return;

        var onTheFile = StillOn(listed, path, isFolder);
        try
        {
            disk.Delete(path, isFolder);
        }
        catch (Exception ex)
        {
            _ = windows.ShowErrorAsync("Delete Failed", $"Could not delete '{Path.GetFileName(path)}':\n{ex.Message}");
            return;
        }

        tabs.Close([.. onTheFile.Select(t => t.Tab)]);
        tree.Remove(path);
    }

    public Task RenameAsync(string path, string newName)
    {
        var newPath = FileTreePaths.Renamed(path, newName, disk.IsDirectory(path));
        return MoveOrRenameAsync(path, newPath, "Rename Failed", "rename", $"to '{Path.GetFileName(newPath)}'");
    }

    /// <summary>Moves a node into the folder of <paramref name="contextPath"/>; dropping it where it already is does nothing.</summary>
    public Task MoveAsync(string sourcePath, string? contextPath)
    {
        var targetFolder = paths.ParentFolder(contextPath, contextPath is not null && disk.IsDirectory(contextPath));
        var newPath = FileTreePaths.Moved(sourcePath, targetFolder);
        if (FileTreePaths.IsSamePlace(newPath, sourcePath)) return Task.CompletedTask;

        return MoveOrRenameAsync(sourcePath, newPath, "Move Failed", "move", $"into '{Path.GetFileName(targetFolder)}'");
    }

    private async Task MoveOrRenameAsync(string source, string destination, string errorTitle, string verb, string destinationPhrase)
    {
        var isFolder = disk.IsDirectory(source);
        var listed = Affected(source, isFolder);
        if (!await tabs.SaveAsync(listed, errorTitle)) return;

        var onTheFile = StillOn(listed, source, isFolder);
        try
        {
            disk.Move(source, destination, isFolder);
        }
        catch (Exception ex)
        {
            _ = windows.ShowErrorAsync(errorTitle, $"Could not {verb} '{Path.GetFileName(source)}' {destinationPhrase}:\n{ex.Message}");
            return;
        }

        tree.Refresh();
        tabs.Close([.. onTheFile.Select(t => t.Tab)]);
        foreach (var (_, current) in onTheFile) tabs.Open(FileTreePaths.Reopened(source, destination, current, isFolder));
    }

    // The open tab on the path itself and, for a folder, every tab below it.
    private List<FileOperationTab> Affected(string path, bool isFolder)
    {
        var open = tabs.OpenTabs;
        var inside = paths.AffectedPaths(open.Select(t => t.Path), path, isFolder).ToHashSet();
        return [.. open.Where(t => inside.Contains(t.Path))];
    }

    // The listed tabs that are still on the path (or below it) after the save, with the file each is on now.
    private List<(FileOperationTab Tab, string Current)> StillOn(List<FileOperationTab> listed, string path, bool isFolder)
    {
        var result = new List<(FileOperationTab, string)>();
        foreach (var tab in listed)
            if (tabs.CurrentPath(tab) is { } current && paths.AffectedPaths([current], path, isFolder).Count == 1)
                result.Add((tab, current));
        return result;
    }
}
