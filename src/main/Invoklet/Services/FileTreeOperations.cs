namespace Invoklet.Services;

/// <summary>The open tabs, as the file tree operations see them: by the paths of their files.</summary>
internal interface IFileOperationTabs
{
    /// <summary>The paths of the files open in tabs.</summary>
    IReadOnlyList<string> OpenPaths { get; }

    /// <summary>Saves the tabs of these files. False when one could not be (the user was told) or the user cancelled: the operation must not go on.</summary>
    Task<bool> SaveAsync(IReadOnlyList<string> paths, string errorTitle);

    void Close(IReadOnlyList<string> paths);

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
/// state, instead of closing them and reading them back from disk.
/// </summary>
internal sealed class FileTreeOperations(
    IDiskOperations disk, IWindowService windows, IFileOperationTabs tabs, IFileTreeSurface tree, FileTreePaths paths)
{
    public async Task DeleteAsync(string path)
    {
        var isFolder = disk.IsDirectory(path);
        var affected = paths.AffectedPaths(tabs.OpenPaths, path, isFolder);
        if (!await tabs.SaveAsync(affected, "Delete Failed")) return;

        try
        {
            disk.Delete(path, isFolder);
        }
        catch (Exception ex)
        {
            _ = windows.ShowErrorAsync("Delete Failed", $"Could not delete '{Path.GetFileName(path)}':\n{ex.Message}");
            return;
        }

        tabs.Close(affected);
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
        var affected = paths.AffectedPaths(tabs.OpenPaths, source, isFolder);
        if (!await tabs.SaveAsync(affected, errorTitle)) return;

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
        tabs.Close(affected);
        foreach (var oldPath in affected) tabs.Open(FileTreePaths.Reopened(source, destination, oldPath, isFolder));
    }
}
