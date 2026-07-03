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
}

public sealed class DotpFileService : IDotpFileService
{
    private const string DotpExtension = ".dotp";
    private const string RpadExtension = ".rpad";

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

    public IReadOnlyList<string> GetFilesInFolder(string folder)
    {
        var dotp = Directory.GetFiles(folder, "*" + DotpExtension);
        var rpad = Directory.GetFiles(folder, "*" + RpadExtension);
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
        // Write the default template; import is resolved at the call site to avoid coupling
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void StartWatcher(string folder)
    {
        _watcher.Path = folder;
        _watcher.Filter = "*.*";
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

    private void OnChanged(object source, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath);
        if (!ext.Equals(DotpExtension, StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(RpadExtension, StringComparison.OrdinalIgnoreCase))
            return;
        _logger.LogDebug("{Name} at {Path} has been {ChangeType}", e.Name, e.FullPath, e.ChangeType);
    }
}
