using System.Diagnostics;
using Invoklet.Format.MdRun;
using Microsoft.Extensions.Logging;

namespace Invoklet.Services;

public record FileSystemItem(string Name, bool IsDirectory, string FilePath, IReadOnlyList<FileSystemItem>? Children);

public interface IFileSystemService : IDisposable
{
    /// <summary>
    /// Recursively walks the workspace folder off the UI thread (the old
    /// synchronous <c>GetFilesTree</c> ran directly on the UI thread, both at startup and on every
    /// settled <see cref="WatcherEvent"/>, freezing input/rendering for the duration of the walk on
    /// large/network/cloud-synced folders).
    /// </summary>
    Task<IReadOnlyList<FileSystemItem>> GetFilesTreeAsync(CancellationToken ct = default);
    string CreateNewFile(string parentDir, MdRunLanguage language, MdRunKind kind, string code);
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

    // Folders listed by the last tree walk. A deleted path can't be asked whether it was a folder,
    // and a folder's name may have a dot in it (project.v2), so this is how a deletion or a rename
    // away of a listed folder is recognised.
    private volatile HashSet<string> _knownFolders = new(StringComparer.Ordinal);
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

    // Tests: a service over a temporary folder instead of Documents/Invoklet Queries.
    internal FileSystemService(ILogger<FileSystemService> logger, string rootFolder)
    {
        _logger = logger;
        _rootFolder = rootFolder;
    }

    private static string GetOrCreateDefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(documents, "Invoklet Queries");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        return folder;
    }

    // internal (not private) + InternalsVisibleTo so tests can exercise this directly against a real
    // temp directory, instead of going through the public GetFilesTree()/the class's constructor —
    // which resolves the real Environment.SpecialFolder.MyDocuments as a side effect.
    internal static IReadOnlyList<FileSystemItem> GetFilesTreeInternal(
        string folder, ILogger? logger = null, CancellationToken ct = default)
    {
        // Checked per folder, so a cancelled refresh (window disposed) stops mid-walk instead of
        // finishing a possibly huge/slow network tree nobody will look at. OperationCanceledException
        // isn't an IOException, so the per-child handlers below let it through.
        ct.ThrowIfCancellationRequested();
        var result = new List<FileSystemItem>();

        // Walk folders first. Don't skip folders with no .rpad files inside (used to have "if
        // (children.Count > 0)" here) — otherwise a folder just created via "New Folder", while still
        // empty, would immediately disappear from the tree on the very next RefreshNodes(), which is
        // exactly what happened: the "New Folder" command really did create the directory on disk, but
        // GetFilesTreeInternal immediately filtered it out as "empty", so it never made it into
        // FileTreeViewModel.Nodes, and FindByPath couldn't find it to enter rename mode.
        //
        // A single unreadable subfolder (permissions) or one deleted between
        // GetDirectories and the recursion used to abort the whole walk — and, through the
        // fire-and-forget refresh, the app. Only the root's own failure propagates now: a vanished
        // child is dropped, an unreadable one is still listed but with no children.
        foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d))
        {
            // Folders whose own name starts with a dot belong to tools, not scripts (.git, .obsidian
            // of a vault kept in the queries folder, .vscode): neither they nor anything inside them
            // is listed or walked. Only the name counts — the root folder itself is always listed
            // from, even when it or one of its parents has such a name.
            if (IsDotDirectory(dir)) continue;

            IReadOnlyList<FileSystemItem> children;
            try
            {
                children = GetFilesTreeInternal(dir, logger, ct);
            }
            catch (DirectoryNotFoundException e)
            {
                logger?.LogWarning(e, "Skipping vanished folder {Folder}", dir);
                continue;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                logger?.LogWarning(e, "Cannot read folder {Folder}", dir);
                children = [];
            }

            result.Add(new FileSystemItem(Path.GetFileName(dir), true, dir, children));
        }

        // Then files
        foreach (var file in Directory
                     .GetFiles(folder, $"*{FileExtension}")
                     .OrderBy(Path.GetFileName))
        {
            result.Add(new FileSystemItem(Path.GetFileName(file), false, file, null));
        }

        return result;
    }

    internal static bool IsDotDirectory(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).StartsWith('.');

    public Task<IReadOnlyList<FileSystemItem>> GetFilesTreeAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                var tree = GetFilesTreeInternal(_rootFolder, _logger, ct);
                _knownFolders = CollectFolders(tree);
                return tree;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Can not build FilesTree");
                throw;
            }
        }, ct);


    // File tree's "New File" — same "probe for a free name" pattern as CreateNewFolder,
    // but for a .rpad file, written into the caller-resolved target directory instead of always the
    // workspace root (the caller — MainWindowViewModel.CreateFileAsync — resolves that the same way
    // CreateFolderAsync does). Written with MdRunWriter.NewFile around the caller's <code> (the
    // language's template for the kind), so the file is a well-formed .rpad document at once.
    public string CreateNewFile(string parentDir, MdRunLanguage language, MdRunKind kind, string code)
    {
        int i = 1;
        string path;
        do
        {
            path = Path.Combine(parentDir, $"Query {i++}{FileExtension}");
        } while (File.Exists(path) || Directory.Exists(path));

        File.WriteAllText(path, MdRunWriter.NewFile(
            code, title: Path.GetFileNameWithoutExtension(path), language: language, kind: kind));
        return path;
    }

    // Same "probe for a free name" pattern as CreateNewFile, but for a directory —
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
        // Everything: a "*.rpad" filter is applied by the system watcher to folder names too, so a
        // folder created or renamed outside Invoklet (project.v2, Imported) never raised an event.
        // Which changes matter is decided in IsRelevant.
        _watcher.Filter = "";
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
        // A folder's own "changed" (its contents or times changed) never alters the tree: whatever
        // changed inside raises an event of its own.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath)
            || !IsRelevant(e.FullPath))
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

    // Whether a change at this path can change the Queries tree: a script, or a listed folder (one
    // that exists now, or that the last tree walk listed — a deleted or renamed-away one). Nothing
    // inside a dot folder is listed, nor is a dot-named folder itself; other files never are.
    private bool IsRelevant(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootFolder, fullPath);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts[..^1].Any(p => p.StartsWith('.')))
            return false;

        if (Path.GetExtension(fullPath).Equals(FileExtension, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(fullPath))
            return true;
        if (parts[^1].StartsWith('.'))
            return false;
        return Directory.Exists(fullPath) || _knownFolders.Contains(Path.TrimEndingDirectorySeparator(fullPath));
    }

    private static HashSet<string> CollectFolders(IEnumerable<FileSystemItem> items)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<FileSystemItem> level)
        {
            foreach (var item in level.Where(i => i.IsDirectory))
            {
                folders.Add(Path.TrimEndingDirectorySeparator(item.FilePath));
                Walk(item.Children ?? []);
            }
        }
        Walk(items);
        return folders;
    }

    public void OpenFolder(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is null)
            return;

        // Windows/macOS can select the file itself within its file manager; Linux has no
        // cross-desktop-environment equivalent to "select this file", so xdg-open just opens the
        // containing folder instead. The Windows branch keeps its original,
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
