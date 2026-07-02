using Microsoft.Extensions.Logging;

namespace DotPad.Build;

public interface IDotpFileService : IDisposable
{
    string GetOrCreateDefaultFolder();
    IReadOnlyList<string> GetFilesInFolder(string folder);
    string CreateNewFile(string folder);
    void StartWatcher(string folder);
    void StopWatcher();
}

public sealed class DotpFileService : IDotpFileService
{
    private const string FileExtension = ".dotp";
    private const string FileFilter = "*" + FileExtension;

    private readonly FileSystemWatcher _watcher = new();
    private readonly ILogger<DotpFileService> _logger;

    public DotpFileService(ILogger<DotpFileService> logger)
    {
        _logger = logger;
    }

    public string GetOrCreateDefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(documents, "DotPad Queries");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        return folder;
    }

    public IReadOnlyList<string> GetFilesInFolder(string folder) =>
        Directory.GetFiles(folder, FileFilter).ToList();

    public string CreateNewFile(string folder)
    {
        int i = 1;
        string path;
        do { path = Path.Combine(folder, $"Query {i++}{FileExtension}"); }
        while (File.Exists(path));
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void StartWatcher(string folder)
    {
        _watcher.Path = folder;
        _watcher.Filter = FileFilter;
        _watcher.IncludeSubdirectories = true;
        _watcher.NotifyFilter =
            NotifyFilters.Attributes | NotifyFilters.CreationTime |
            NotifyFilters.DirectoryName | NotifyFilters.FileName |
            NotifyFilters.LastAccess | NotifyFilters.LastWrite |
            NotifyFilters.Security | NotifyFilters.Size;

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;

        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatcher() => _watcher.EnableRaisingEvents = false;

    public void Dispose() => _watcher.Dispose();

    private void OnChanged(object source, FileSystemEventArgs e) =>
        _logger.LogDebug("{Name} at {Path} has been {ChangeType}", e.Name, e.FullPath, e.ChangeType);
}
