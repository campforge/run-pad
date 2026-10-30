namespace Invoklet.Services;

/// <summary>What the file tree operations do to the disk: ask whether a path is a folder, delete it, move it.</summary>
public interface IDiskOperations
{
    bool IsDirectory(string path);

    /// <summary>Deletes a file, or a folder with everything in it. Throws what the file system throws.</summary>
    void Delete(string path, bool isDirectory);

    /// <summary>Moves or renames a file or a folder. Throws what the file system throws.</summary>
    void Move(string source, string destination, bool isDirectory);
}

/// <summary>The disk itself. A file and a folder are not interchangeable: <c>File.Delete</c> and <c>File.Move</c> fail on a folder.</summary>
public sealed class DiskOperations : IDiskOperations
{
    public bool IsDirectory(string path) => Directory.Exists(path);

    public void Delete(string path, bool isDirectory)
    {
        if (isDirectory) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    public void Move(string source, string destination, bool isDirectory)
    {
        if (isDirectory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}
