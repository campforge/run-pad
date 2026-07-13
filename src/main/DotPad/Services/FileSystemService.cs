using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotPad.Services;

public record FileSystemItem(string Name, bool IsDirectory, string FilePath, IReadOnlyList<FileSystemItem>? Children);

public interface IFileSystemService : IDisposable
{
    IReadOnlyList<FileSystemItem> GetFilesTree();
    string CreateNewFile();
    string CreateNewFolder(string parentDir);
    void StartWatcher();
    void StopWatcher();

    string Folder { get; }

    /// <summary>
    /// Raised on the thread-pool when a tracked file (.rpad) or a directory
    /// inside the watched folder is created, deleted, renamed, or changed.
    /// For Renamed events the argument is <see cref="RenamedEventArgs"/>.
    /// </summary>
    event EventHandler<FileSystemEventArgs>? WatcherEvent;

    void OpenFolder(string dir);
}

public sealed class FileSystemService : IFileSystemService
{
    private const string FileExtension = ".rpad";
    private readonly string _rootFolder;

    private readonly FileSystemWatcher _watcher = new();
    private readonly ILogger<FileSystemService> _logger;

    public event EventHandler<FileSystemEventArgs>? WatcherEvent;


    public string Folder => _rootFolder;

    public FileSystemService(
        ILogger<FileSystemService> logger
    )
    {
        _logger = logger;
        _rootFolder = GetOrCreateDefaultFolder();
    }

    private static string GetOrCreateDefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(documents, "DotPad Queries");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        return folder;
    }

    // internal (not private) + InternalsVisibleTo so tests can exercise this directly against a real
    // temp directory, instead of going through the public GetFilesTree()/the class's constructor —
    // which resolves the real Environment.SpecialFolder.MyDocuments as a side effect (see
    // ANALYSIS.md TODO #69 follow-up).
    internal static IReadOnlyList<FileSystemItem> GetFilesTreeInternal(string folder)
    {
        var result = new List<FileSystemItem>();

        // Идем сперва по папкам. Не пропускаем папки без .rpad-файлов внутри (было раньше, "if
        // (children.Count > 0)") — иначе только что созданная через TODO #69 пустая папка тут же
        // исчезала бы из дерева на первом же RefreshNodes(), что и произошло: команда "New Folder"
        // реально создавала директорию на диске, но GetFilesTreeInternal тут же отфильтровывал её
        // как "пустую", так что она никогда не попадала в FileTreeViewModel.Nodes, и FindByPath не
        // мог её найти для входа в режим переименования.
        foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d))
        {
            var children = GetFilesTreeInternal(dir);
            result.Add(new FileSystemItem(Path.GetFileName(dir), true, dir, children));
        }

        // Потом по файлам
        foreach (var file in Directory
                     .GetFiles(folder, $"*{FileExtension}")
                     .OrderBy(Path.GetFileName))
        {
            result.Add(new FileSystemItem(Path.GetFileName(file), false, file, null));
        }

        return result;
    }

    public IReadOnlyList<FileSystemItem> GetFilesTree()
    {
        try
        {
            return GetFilesTreeInternal(_rootFolder);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Can not build FilesTree");
            throw;
        }
        // return new List<FileSystemItem>();
    }


    public string CreateNewFile()
    {
        int i = 1;
        string path;
        do
        {
            path = Path.Combine(_rootFolder, $"Query {i++}{FileExtension}");
        } while (File.Exists(path));

        File.WriteAllText(path, string.Empty);
        return path;
    }

    // TODO.md #69: same "probe for a free name" pattern as CreateNewFile, but for a directory —
    // "New Folder", "New Folder 2", "New Folder 3", ... under the given parent (the caller — see
    // MainWindowViewModel.CreateFolder — resolves whether that's the selected folder itself, the
    // selected file's containing folder, or the tree's root).
    public string CreateNewFolder(string parentDir)
    {
        int i = 1;
        string path;
        do
        {
            path = Path.Combine(parentDir, i == 1 ? "New Folder" : $"New Folder {i}");
            i++;
        } while (Directory.Exists(path) || File.Exists(path));

        Directory.CreateDirectory(path);
        return path;
    }

    public void StartWatcher()
    {
        _watcher.Path = _rootFolder;
        _watcher.Filter = $"*{FileExtension}";
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
        if (!IsRelevant(e.FullPath))
        {
            return;
        }

        _logger.LogDebug("{Name} {ChangeType}", e.Name, e.ChangeType);
        WatcherEvent?.Invoke(source, e);
    }

    private void OnRenamedEvent(object source, RenamedEventArgs e)
    {
        if (!IsRelevant(e.FullPath) && !IsRelevant(e.OldFullPath))
        {
            return;
        }

        _logger.LogDebug("{OldName} → {Name} (Renamed)", e.OldName, e.Name);
        WatcherEvent?.Invoke(source, e);
    }

    // Fire for: .rpad files or items with no extension (directories)
    private static bool IsRelevant(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        return string.IsNullOrEmpty(ext)
               || ext.Equals(FileExtension, StringComparison.OrdinalIgnoreCase);
    }

    public void OpenFolder(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is null)
            return;

        // Windows/macOS can select the file itself within its file manager; Linux has no
        // cross-desktop-environment equivalent to "select this file", so xdg-open just opens the
        // containing folder instead (see ANALYSIS.md #26). The Windows branch keeps its original,
        // already-working `/select,"path"` form (single Arguments string, not ArgumentList — that
        // exact comma-then-quoted-path syntax is what explorer.exe expects) unverified changes to
        // it aren't worth the risk since Windows isn't available to test in this environment.
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            { FileName = "explorer.exe", Arguments = $"/select,\"{filePath}\"", UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo { FileName = "open", ArgumentList = { "-R", filePath } }
                : new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { dir } };
        Process.Start(startInfo);
    }
}
