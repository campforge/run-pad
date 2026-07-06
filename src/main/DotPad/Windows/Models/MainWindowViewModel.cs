using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using DotPad.Assets.Models;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using DotPad.UserControls.Models;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IFileSystemService _fileSystemSystemService;
    private readonly IWindowService _windowService;
    private readonly IPreferencesService _prefs;

    private readonly Dictionary<string, EditorTabItemViewModel> _openTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EditorTabItemViewModel, IDisposable> _closeSubscriptions = new();

    // Debounce state for watcher-triggered refreshes
    private volatile int _debounceVersion;

    // ── Window commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> OpenDependenciesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAboutCommand { get; }

    // Raised after the Dependencies dialog closes, so the view can refresh IntelliSense for the
    // (possibly package-mutated) active tab. RpadFrontMatter has no change notification of its own.
    public event Action? DependenciesWindowClosed;

    public void OpenDependenciesWindow() => _ = OpenDependenciesWindowAsync();

    private async Task OpenDependenciesWindowAsync()
    {
        await _windowService.ShowDependenciesAsync(ActiveTab?.FrontMatter);
        DependenciesWindowClosed?.Invoke();
    }

    // ── Sidebar layout (collapsible file tree) ────────────────────────────────

    private double _lastExpandedSidebarWidth;

    public GridLength SidebarColumnWidth
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }

    private void ToggleSidebar()
    {
        if (SidebarColumnWidth.Value > 0)
        {
            _lastExpandedSidebarWidth = SidebarColumnWidth.Value;
            SidebarColumnWidth = new GridLength(0);
        }
        else
        {
            SidebarColumnWidth = new GridLength(_lastExpandedSidebarWidth > 0 ? _lastExpandedSidebarWidth : 300);
        }
    }

    // ── Tab collection ────────────────────────────────────────────────────────

    public ObservableCollection<EditorTabItemViewModel> Tabs { get; } = [];

    private EditorTabItemViewModel? _activeTab;

    public EditorTabItemViewModel? ActiveTab
    {
        get => _activeTab;
        set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    public void NewRpadFile()
    {
        var path = _fileSystemSystemService.CreateNewFile();
        var title = Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, RpadWriter.NewFile(title), System.Text.Encoding.UTF8);
        Nodes.Add(new Node(Path.GetFileName(path), path));
        OpenTab(path);
    }

    public void OpenTab(string? filePath)
    {
        if (filePath is not null && _openTabs.TryGetValue(filePath, out var existing))
        {
            ActiveTab = existing;
            return;
        }

        // Close the initial blank tab when the first real file is opened.
        EditorTabItemViewModel? blankToClose = null;
        if (filePath is not null && Tabs.Count == 1 && Tabs[0].FilePath is null)
            blankToClose = Tabs[0];

        var vm = new EditorTabItemViewModel(filePath, AvailableSdks, _prefs);
        _closeSubscriptions[vm] = vm.CloseCommand.Subscribe(_ => CloseTab(vm));

        if (filePath is not null) _openTabs[filePath] = vm;
        Tabs.Add(vm);
        ActiveTab = vm;

        if (blankToClose is not null)
            CloseTab(blankToClose);
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

        try
        {
            File.Delete(node.FilePath);
        }
        catch
        {
            /* ignore */
        }

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

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            node.EditingName = node.DisplayName;
            // The tab was already closed above (so SaveIfNeeded could flush to oldPath) — since the
            // rename didn't happen, the file is still at oldPath, so reopen it instead of leaving its
            // tab silently gone from the tab bar.
            if (hadTab) OpenTab(oldPath);
            _ = _windowService.ShowErrorAsync(
                "Rename Failed",
                $"Could not rename '{Path.GetFileName(oldPath)}' to '{newFileName}':\n{ex.Message}");
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
        try
        {
            _fileSystemSystemService.OpenFolder(node.FilePath);
        }
        catch (Exception ex)
        {
            _ = _windowService.ShowErrorAsync("Could Not Open File Manager", ex.Message);
        }
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    public ObservableCollection<Node> Nodes { get; }
    public List<DotnetSdk> AvailableSdks { get; }

    public MainWindowViewModel(
        IEnumerable<ISdkProvider> sdkProviders,
        IFileSystemService fileSystemSystemService,
        IWindowService windowService,
        IPreferencesService prefs)
    {
        _fileSystemSystemService = fileSystemSystemService;
        _windowService = windowService;
        _prefs = prefs;
        OpenDependenciesCommand = ReactiveCommand.CreateFromTask(OpenDependenciesWindowAsync);
        OpenPreferencesCommand = ReactiveCommand.CreateFromTask(_windowService.ShowPreferencesAsync);
        OpenAboutCommand = ReactiveCommand.CreateFromTask(_windowService.ShowAboutAsync);
        OpenDependenciesCommand.ThrownExceptions.Subscribe(_ => { });
        OpenPreferencesCommand.ThrownExceptions.Subscribe(_ => { });
        OpenAboutCommand.ThrownExceptions.Subscribe(_ => { });
        AvailableSdks = sdkProviders.SelectMany(p => p.Get()).ToList();

        SidebarColumnWidth = _prefs.Layout.SidebarCollapsed
            ? new GridLength(0)
            : new GridLength(_prefs.Layout.SidebarWidth);
        _lastExpandedSidebarWidth = _prefs.Layout.SidebarWidth;
        ToggleSidebarCommand = ReactiveCommand.Create(ToggleSidebar);
        this.WhenAnyValue(x => x.SidebarColumnWidth)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(400), RxApp.MainThreadScheduler)
            .Subscribe(w =>
            {
                _prefs.Layout.SidebarCollapsed = w.Value <= 0;
                if (w.Value > 0) _prefs.Layout.SidebarWidth = w.Value;
                _prefs.Save();
            });
        Nodes = [];
        RefreshNodes();

        _fileSystemSystemService.WatcherEvent += OnWatcherEvent;
        _fileSystemSystemService.StartWatcher();
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
        var filesTree = _fileSystemSystemService.GetFilesTree();
        MergeNodeList(Nodes, filesTree);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    private static Node MapToNode(FileSystemItem fsi) => fsi.IsDirectory
        ? new Node(fsi.Name, fsi.FilePath, new ObservableCollection<Node>((fsi.Children ?? []).Select(MapToNode)))
        : new Node(fsi.Name, fsi.FilePath);

    /// <summary>Merges <paramref name="fresh"/> into <paramref name="current"/> with minimal
    /// ObservableCollection mutations to preserve UI state (selection, expanded folders).</summary>
    private static void MergeNodeList(ObservableCollection<Node> current, IReadOnlyList<FileSystemItem> fresh)
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
                MergeNodeList(existing.SubNodes, freshFolder.Children ?? []);
        }

        // 3. Add nodes that are missing from current (new files/folders)
        var currentPaths = new HashSet<string>(
            current.Select(n => n.FilePath), StringComparer.OrdinalIgnoreCase);

        int insertIndex = 0;

        foreach (var freshNode in fresh)
        {
            if (!currentPaths.Contains(freshNode.FilePath))
                current.Insert(Math.Min(insertIndex, current.Count), MapToNode(freshNode));
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
        _fileSystemSystemService.WatcherEvent -= OnWatcherEvent;
        _fileSystemSystemService.StopWatcher();
        foreach (var d in _closeSubscriptions.Values)
        {
            d.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
