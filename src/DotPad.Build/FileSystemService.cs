namespace DotPad.Build;

// singleton
public class FileSystemService : IDisposable
{
    private const string FileExtension = ".dotp";
    private const string FileFilter = "*" + FileExtension;

    FileSystemWatcher _watcher = new();

    public string GetOrCreateDefaultProFilesFolder()
    {
        var userProfileFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var result = Path.Combine(userProfileFolder, "DotPad Queries");

        if (!Directory.Exists(result))
            Directory.CreateDirectory(result);

        return result;
    }

    public List<string> GetFilesInFolder(string folder)
    {
        return Directory
            .GetFiles(folder, FileFilter)
            .Select(s => Path.GetFileName(s!))
            .ToList();
    }

    public void StartWatcher(string folder)
    {
        _watcher.Path = folder;
        _watcher.Filter = FileFilter;
        _watcher.IncludeSubdirectories = true;
        _watcher.NotifyFilter = NotifyFilters.Attributes |
                                NotifyFilters.CreationTime |
                                NotifyFilters.DirectoryName |
                                NotifyFilters.FileName |
                                NotifyFilters.LastAccess |
                                NotifyFilters.LastWrite |
                                NotifyFilters.Security |
                                NotifyFilters.Size;
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;

        _watcher.EnableRaisingEvents = true;
    }

    private static void OnChanged(object source, FileSystemEventArgs e)
    {
        Console.WriteLine("{0}, with path {1} has been {2}", e.Name, e.FullPath, e.ChangeType);
    }

    public void StopWatcher()
    {
        _watcher.EnableRaisingEvents = false;
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}