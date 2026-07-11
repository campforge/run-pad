using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
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

    // OrdinalIgnoreCase matches Windows'/macOS' default case-insensitive filesystem; Linux's is
    // case-sensitive, where it would incorrectly collapse two distinct files differing only in
    // case (e.g. Query1.rpad/query1.rpad) into one dictionary entry.
    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly Dictionary<string, EditorTabItemViewModel> _openTabs = new(FilePathComparer);
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
        await _windowService.ShowDependenciesAsync(ActiveTab?.WhenAnyValue(x => x.FrontMatter));
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

        RefreshNodes();
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

    public void DeleteFile(string filePath)
    {
        // Close tab first so SaveIfNeeded writes content, THEN delete the file.
        if (_openTabs.TryGetValue(filePath, out var tab))
            CloseTab(tab);

        try
        {
            File.Delete(filePath);
        }
        catch
        {
            /* ignore */
        }

        FileTree.RemoveByPath(filePath);
    }

    public void RenameFile(string filePath, string newName)
    {
        var ext = Path.GetExtension(filePath);
        var dir = Path.GetDirectoryName(filePath)!;
        var newFileName = newName + ext;
        var newPath = Path.Combine(dir, newFileName);

        // Close tab first so SaveIfNeeded writes current content to OLD path.
        var hadTab = _openTabs.ContainsKey(filePath);
        if (_openTabs.TryGetValue(filePath, out var tab))
            CloseTab(tab);

        try
        {
            File.Move(filePath, newPath);
        }
        catch (Exception ex)
        {
            // The tab was already closed above (so SaveIfNeeded could flush to the old path) — since
            // the rename didn't happen, the file is still at the old path, so reopen it instead of
            // leaving its tab silently gone from the tab bar.
            if (hadTab) OpenTab(filePath);
            _ = _windowService.ShowErrorAsync(
                "Rename Failed",
                $"Could not rename '{Path.GetFileName(filePath)}' to '{newFileName}':\n{ex.Message}");
            return;
        }

        RefreshNodes();

        if (hadTab)
            OpenTab(newPath);
    }

    public void OpenContainingFolder(string filePath)
    {
        try
        {
            _fileSystemSystemService.OpenFolder(filePath);
        }
        catch (Exception ex)
        {
            _ = _windowService.ShowErrorAsync("Could Not Open File Manager", ex.Message);
        }
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    public FileTreeViewModel FileTree { get; } = new();

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

        RefreshNodes();

        _fileSystemSystemService.WatcherEvent += OnWatcherEvent;
        _fileSystemSystemService.StartWatcher();
    }

    // ── Watcher ───────────────────────────────────────────────────────────────

    private void OnWatcherEvent(object? sender, FileSystemEventArgs e)
    {
        // Version-based debounce: capture current version, wait 300 ms,
        // then refresh only if no newer event arrived.
        var version = Interlocked.Increment(ref _debounceVersion);
        Task.Delay(300).ContinueWith(_ =>
        {
            if (_debounceVersion == version)
                Dispatcher.UIThread.Post(RefreshNodes);
        });
    }

    private void RefreshNodes() => FileTree.Refresh(_fileSystemSystemService.GetFilesTree());

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
