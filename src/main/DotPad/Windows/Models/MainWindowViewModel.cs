using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;
using Avalonia.Threading;
using DotPad.Assets.Models;
using DotPad.Build;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using DotPad.UserControls.Models;
using DynamicData;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IDotpFileService _fileSystemService;
    private readonly IWindowService _windowService;
    private readonly IPreferencesService _prefs;
    private readonly string _rootFolder;

    private readonly Dictionary<string, EditorTabItemViewModel> _openTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EditorTabItemViewModel, IDisposable> _closeSubscriptions = new();

    // Debounce state for watcher-triggered refreshes
    // BUG-5: должно быть volatile — чтение без барьера памяти может взять кэшированное значение.
    private volatile int _debounceVersion;

    // ── Window commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> OpenDependenciesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }

    public void OpenDependenciesWindow() => _ = _windowService.ShowDependenciesAsync(ActiveTab?.FrontMatter);
    public void OpenPreferencesWindow()  => _ = _windowService.ShowPreferencesAsync();

    // ── Tab collection ────────────────────────────────────────────────────────

    public ObservableCollection<EditorTabItemViewModel> Tabs { get; } = [];

    private EditorTabItemViewModel? _activeTab;
    public EditorTabItemViewModel? ActiveTab
    {
        get => _activeTab;
        set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    public EditorTabItemViewModel NewRpadFile()
    {
        var path = _fileSystemService.CreateNewRpadFile(_rootFolder);
        var title = Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, RpadWriter.NewFile(title), System.Text.Encoding.UTF8);
        Nodes.Add(new Node(Path.GetFileName(path), path));
        return OpenTab(path);
    }

    public EditorTabItemViewModel OpenTab(string? filePath)
    {
        if (filePath is not null && _openTabs.TryGetValue(filePath, out var existing))
        {
            ActiveTab = existing;
            return existing;
        }

        var vm = new EditorTabItemViewModel(filePath, DotnetPlatforms, _prefs);
        _closeSubscriptions[vm] = vm.CloseCommand.Subscribe(_ => CloseTab(vm));

        if (filePath is not null) _openTabs[filePath] = vm;
        Tabs.Add(vm);
        ActiveTab = vm;
        return vm;
    }

    internal void CloseActiveTab()
    {
        if (_activeTab != null) CloseTab(_activeTab);
    }

    private void CloseTab(EditorTabItemViewModel vm)
    {
        if (_closeSubscriptions.TryGetValue(vm, out var d))
        {
            d.Dispose();
            _closeSubscriptions.Remove(vm);
        }
        if (vm.FilePath is not null) _openTabs.Remove(vm.FilePath);

        bool wasActive = ActiveTab == vm;
        Tabs.Remove(vm);
        if (wasActive) ActiveTab = Tabs.LastOrDefault();
    }

    // ── File tree operations ──────────────────────────────────────────────────

    public void DeleteNode(Node node)
    {
        // Close tab first so SaveIfNeeded writes content, THEN delete the file.
        if (_openTabs.TryGetValue(node.FilePath, out var tab))
            CloseTab(tab);

        try { File.Delete(node.FilePath); } catch { /* ignore */ }

        RemoveNodeByPath(Nodes, node.FilePath);
    }

    public void BeginRenameNode(Node node)
    {
        node.EditingName = node.DisplayName;
        node.IsRenaming = true;
    }

    public void CommitRenameNode(Node node)
    {
        node.IsRenaming = false;

        var newName = node.EditingName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == node.DisplayName)
            return;

        var ext = Path.GetExtension(node.FilePath);
        var dir = Path.GetDirectoryName(node.FilePath)!;
        var newFileName = newName + ext;
        var newPath = Path.Combine(dir, newFileName);
        var oldPath = node.FilePath;

        // Close tab first so SaveIfNeeded writes current content to OLD path.
        var hadTab = _openTabs.ContainsKey(oldPath);
        if (_openTabs.TryGetValue(oldPath, out var tab))
            CloseTab(tab);

        try { File.Move(oldPath, newPath); }
        catch
        {
            node.EditingName = node.DisplayName;
            return;
        }

        node.FilePath = newPath;
        node.Title = newFileName;

        if (hadTab)
            OpenTab(newPath);
    }

    public void CancelRenameNode(Node node)
    {
        node.IsRenaming = false;
    }

    public void OpenContainingFolder(Node node)
    {
        var dir = Path.GetDirectoryName(node.FilePath);
        if (dir is null) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{node.FilePath}\"",
            UseShellExecute = true
        });
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    public ObservableCollection<Node> Nodes { get; }
    public List<ExecutionPlatform> DotnetPlatforms { get; }

    public MainWindowViewModel(
        IEnumerable<ISdkProvider> sdkProviders,
        IDotpFileService fileSystemService,
        IWindowService windowService,
        IPreferencesService prefs)
    {
        _fileSystemService = fileSystemService;
        _windowService = windowService;
        _prefs = prefs;
        OpenDependenciesCommand = ReactiveCommand.CreateFromTask(() => _windowService.ShowDependenciesAsync(ActiveTab?.FrontMatter));
        OpenPreferencesCommand  = ReactiveCommand.CreateFromTask(_windowService.ShowPreferencesAsync);
        OpenDependenciesCommand.ThrownExceptions.Subscribe(_ => { });
        OpenPreferencesCommand.ThrownExceptions.Subscribe(_ => { });
        DotnetPlatforms = sdkProviders.SelectMany(p => p.Get()).ToList();

        _rootFolder = _fileSystemService.GetOrCreateDefaultFolder();

        Nodes = new ObservableCollection<Node>(BuildNodesFromDisk(_rootFolder));

        _fileSystemService.WatcherEvent += OnWatcherEvent;
        _fileSystemService.StartWatcher(_rootFolder);
    }

    // ── Watcher ───────────────────────────────────────────────────────────────

    private void OnWatcherEvent(object? sender, FileSystemEventArgs e)
    {
        // Version-based debounce: capture current version, wait 300 ms,
        // then refresh only if no newer event arrived.
        var version = System.Threading.Interlocked.Increment(ref _debounceVersion);
        Task.Delay(300).ContinueWith(_ =>
        {
            if (_debounceVersion == version)
                Dispatcher.UIThread.Post(RefreshNodes);
        });
    }

    /// <summary>Rebuilds the Nodes tree from disk, preserving existing Node
    /// objects where possible so selection and expand state survive small changes.</summary>
    private void RefreshNodes()
    {
        var fresh = BuildNodesFromDisk(_rootFolder);
        MergeNodeList(Nodes, fresh);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    private static List<Node> BuildNodesFromDisk(string folder)
    {
        var result = new List<Node>();
        try
        {
            // Folders first (alphabetically), but only if they contain tracked files
            foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d))
            {
                var children = BuildNodesFromDisk(dir);
                if (children.Count > 0)
                    result.Add(new Node(Path.GetFileName(dir), dir,
                        new ObservableCollection<Node>(children)));
            }

            // Then tracked files
            foreach (var file in Directory.GetFiles(folder)
                         .Where(IsTrackedFile)
                         .OrderBy(Path.GetFileName))
            {
                result.Add(new Node(Path.GetFileName(file), file));
            }
        }
        catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }

        return result;
    }

    private static bool IsTrackedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".rpad", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Merges <paramref name="fresh"/> into <paramref name="current"/> with minimal
    /// ObservableCollection mutations to preserve UI state (selection, expanded folders).</summary>
    private static void MergeNodeList(ObservableCollection<Node> current, List<Node> fresh)
    {
        // Build a lookup of fresh paths for O(1) access
        var freshByPath = fresh.ToDictionary(n => n.FilePath, StringComparer.OrdinalIgnoreCase);

        // 1. Remove nodes that no longer exist on disk
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!freshByPath.ContainsKey(current[i].FilePath))
                current.RemoveAt(i);
        }

        // 2. For surviving folder nodes, merge their children recursively
        foreach (var existing in current)
        {
            if (existing.SubNodes is not null && freshByPath.TryGetValue(existing.FilePath, out var freshFolder))
                MergeNodeList(existing.SubNodes, freshFolder.SubNodes?.ToList() ?? []);
        }

        // 3. Add nodes that are missing from current (new files/folders)
        var currentPaths = new HashSet<string>(
            current.Select(n => n.FilePath), StringComparer.OrdinalIgnoreCase);

        int insertIndex = 0;
        foreach (var freshNode in fresh)
        {
            if (!currentPaths.Contains(freshNode.FilePath))
                current.Insert(Math.Min(insertIndex, current.Count), freshNode);
            insertIndex++;
        }
    }

    private static bool RemoveNodeByPath(ObservableCollection<Node> nodes, string filePath)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                nodes.RemoveAt(i);
                return true;
            }
            if (nodes[i].SubNodes is { } sub && RemoveNodeByPath(sub, filePath))
                return true;
        }
        return false;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _fileSystemService.WatcherEvent -= OnWatcherEvent;
        _fileSystemService.StopWatcher();
        foreach (var d in _closeSubscriptions.Values) d.Dispose();
        GC.SuppressFinalize(this);
    }
}
