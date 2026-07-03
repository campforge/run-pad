using Microsoft.Extensions.Logging;

namespace DotPad.Build;

public interface IDotpFileService : IDisposable
{
    string GetOrCreateDefaultFolder();
    IReadOnlyList<string> GetFilesInFolder(string folder);
    string CreateNewFile(string folder);
    string CreateNewRpadFile(string folder);
    void StartWatcher(string folder);
    void StopWatcher();

    /// <summary>
    /// Raised on the thread-pool when a tracked file (.rpad/.dotp) or a directory
    /// inside the watched folder is created, deleted, renamed, or changed.
    /// For Renamed events the argument is <see cref="RenamedEventArgs"/>.
    /// </summary>
    event EventHandler<FileSystemEventArgs>? WatcherEvent;
}

public sealed class DotpFileService : IDotpFileService
{
    private const string DotpExtension = ".dotp";
    private const string RpadExtension = ".rpad";

    private readonly FileSystemWatcher _watcher = new();
    private readonly ILogger<DotpFileService> _logger;

    public event EventHandler<FileSystemEventArgs>? WatcherEvent;

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

    public IReadOnlyList<string> GetFilesInFolder(string folder)
    {
        var dotp = Directory.GetFiles(folder, "*" + DotpExtension, SearchOption.AllDirectories);
        var rpad = Directory.GetFiles(folder, "*" + RpadExtension, SearchOption.AllDirectories);
        return dotp.Concat(rpad).OrderBy(Path.GetFileName).ToList();
    }

    public string CreateNewFile(string folder)
    {
        int i = 1;
        string path;
        do { path = Path.Combine(folder, $"Query {i++}{DotpExtension}"); }
        while (File.Exists(path));
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public string CreateNewRpadFile(string folder)
    {
        int i = 1;
        string path;
        do { path = Path.Combine(folder, $"Query {i++}{RpadExtension}"); }
        while (File.Exists(path));
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void StartWatcher(string folder)
    {
        _watcher.Path = folder;
        _watcher.Filter = "*.*";
        _watcher.IncludeSubdirectories = true;
        _watcher.NotifyFilter =
            NotifyFilters.FileName | NotifyFilters.DirectoryName |
            NotifyFilters.LastWrite | NotifyFilters.CreationTime;

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamedEvent;

        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatcher() => _watcher.EnableRaisingEvents = false;

    public void Dispose() => _watcher.Dispose();

    private void OnFileEvent(object source, FileSystemEventArgs e)
    {
        if (!IsRelevant(e.FullPath)) return;
        _logger.LogDebug("{Name} {ChangeType}", e.Name, e.ChangeType);
        WatcherEvent?.Invoke(source, e);
    }

    private void OnRenamedEvent(object source, RenamedEventArgs e)
    {
        if (!IsRelevant(e.FullPath) && !IsRelevant(e.OldFullPath)) return;
        _logger.LogDebug("{OldName} → {Name} (Renamed)", e.OldName, e.Name);
        WatcherEvent?.Invoke(source, e);
    }

    // Fire for: tracked file types (.rpad/.dotp) or items with no extension (directories)
    private static bool IsRelevant(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        return string.IsNullOrEmpty(ext)
            || ext.Equals(DotpExtension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(RpadExtension, StringComparison.OrdinalIgnoreCase);
    }
}
