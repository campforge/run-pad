namespace DotPad.Build;

public interface IDotpFileService : IDisposable
{
    string GetOrCreateDefaultFolder();
    IReadOnlyList<string> GetFilesInFolder(string folder);
    void StartWatcher(string folder);
    void StopWatcher();
}

public sealed class DotpFileService : IDotpFileService
{
    private const string FileExtension = ".dotp";
    private const string FileFilter = "*" + FileExtension;

    private readonly FileSystemWatcher _watcher = new();

    public string GetOrCreateDefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(documents, "DotPad Queries");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        return folder;
    }

    public IReadOnlyList<string> GetFilesInFolder(string folder) =>
        Directory
            .GetFiles(folder, FileFilter)
            .Select(Path.GetFileName)
            .ToList()!;

    public void StartWatcher(string folder)
    {
        _watcher.Path = folder;
        _watcher.Filter = FileFilter;
        _watcher.IncludeSubdirectories = true;
        _watcher.NotifyFilter =
            NotifyFilters.Attributes    | NotifyFilters.CreationTime |
            NotifyFilters.DirectoryName | NotifyFilters.FileName     |
            NotifyFilters.LastAccess    | NotifyFilters.LastWrite    |
            NotifyFilters.Security      | NotifyFilters.Size;

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;

        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatcher() => _watcher.EnableRaisingEvents = false;

    public void Dispose() => _watcher.Dispose();

    private static void OnChanged(object source, FileSystemEventArgs e) =>
        Console.WriteLine("{0}, with path {1} has been {2}", e.Name, e.FullPath, e.ChangeType);
}
