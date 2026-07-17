using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using DotPad.UserControls.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly IWindowService _windowService;
    private readonly IPreferencesService _prefs;
    private readonly ILogger<MainWindowViewModel> _logger;

    // Debounce before persisting a resized sidebar width to preferences — avoids writing to disk on
    // every intermediate drag frame (same reasoning as EditorTabItemViewModel.LayoutSaveThrottle,
    // ANALYSIS.md #94).
    private static readonly TimeSpan LayoutSaveThrottle = TimeSpan.FromMilliseconds(400);

    // Debounce for file-watcher-triggered tree refreshes — a single file save/rename can raise
    // several FileSystemWatcher events in quick succession; wait for them to settle before
    // rebuilding the tree once.
    private static readonly TimeSpan WatcherRefreshDebounce = TimeSpan.FromMilliseconds(300);

    // OrdinalIgnoreCase matches Windows'/macOS' default case-insensitive filesystem; Linux's is
    // case-sensitive, where it would incorrectly collapse two distinct files differing only in
    // case (e.g. Query1.rpad/query1.rpad) into one dictionary entry.
    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    // Same case-(in)sensitivity as FilePathComparer above, but as a StringComparison — needed for
    // "is this tab's path nested inside the folder being deleted/renamed" prefix checks, which
    // StringComparer (only usable for equality/sorting) can't express.
    private static readonly StringComparison FilePathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly Dictionary<string, EditorTabItemViewModel> _openTabs = new(FilePathComparer);
    private readonly Dictionary<EditorTabItemViewModel, IDisposable> _closeSubscriptions = new();

    // Debounce state for watcher-triggered refreshes
    private volatile int _debounceVersion;

    // ── Window commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> OpenDependenciesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAboutCommand { get; }

    // TODO.md #80: the F4 gesture behind OpenDependenciesCommand already worked (OnWindowKeyDown), it
    // just wasn't shown anywhere — this feeds MainWindow.axaml's MenuItem.InputGesture (Avalonia has
    // no WPF-style InputGestureText string property; InputGesture takes a real KeyGesture and renders
    // its own formatted text, same as CodeTextEditor.CreateEditMenuItem's Copy/Cut/Paste items — TODO
    // #34). Not re-evaluated live on a Preferences edit (unlike EditorTabItemViewModel's tooltip
    // labels, which do resubscribe to _prefs.Changed) — a known, minor gap: the menu hint can go
    // stale until restart if the user rebinds this gesture, while the actual keyboard dispatch always
    // reads the current value fresh on every keypress regardless.
    public KeyGesture? OpenDependenciesGesture
    {
        get
        {
            try { return KeyGesture.Parse(_prefs.Keyboard.OpenDependencies); }
            catch { return null; }
        }
    }

    // Feeds MainWindow.axaml's File > Save MenuItem.InputGesture — same rationale as
    // OpenDependenciesGesture above.
    public KeyGesture? SaveGesture
    {
        get
        {
            try { return KeyGesture.Parse(_prefs.Keyboard.Save); }
            catch { return null; }
        }
    }

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

    // Deliberately doesn't touch disk (TODO.md, "Жизненный цикл вкладок") — a new script starts
    // empty and in-memory-only; it only becomes a real file the first time the user does Save or
    // Save As (SaveTabAsAsync below). language/kind are carried on the tab itself
    // (EditorTabItemViewModel.NewFileLanguage/NewFileKind) since there's no front matter on disk yet
    // for EditorsTabItemControl.SetDocumentContent/EditorTabsControl.CreateControlAsync to read them
    // from.
    public void NewRpadFile(RpadLanguage language = RpadLanguage.CSharp, RpadKind kind = RpadKind.Statements)
    {
        var vm = new EditorTabItemViewModel(null, AvailableSdks, _prefs, language, kind);
        AddTab(vm);
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
        if (filePath is not null) _openTabs[filePath] = vm;
        AddTab(vm);

        if (blankToClose is not null)
            CloseTab(blankToClose);
    }

    private void AddTab(EditorTabItemViewModel vm)
    {
        var subs = new CompositeDisposable
        {
            vm.CloseCommand.Subscribe(_ => CloseTab(vm)),
            vm.OpenContainingFolderCommand.Subscribe(_ =>
            {
                if (vm.FilePath is { } path) OpenContainingFolder(path);
            }),
        };
        _closeSubscriptions[vm] = subs;

        Tabs.Add(vm);
        ActiveTab = vm;
    }

    // Called after a Save As (or the first Save of a brand-new, never-saved tab) actually writes
    // `newPath` to disk — rebinds the tab from "untitled" (or its old path) to the new one, the same
    // bookkeeping RenameFile already does for a file-tree rename, just triggered from the tab side
    // instead of the tree.
    public async Task SaveTabAsAsync(EditorTabItemViewModel vm, Action<string> writeToPath)
    {
        var newPath = await _windowService.ShowSaveFileDialogAsync(vm.FileName, _fileSystemService.Folder);
        if (newPath is null) return; // cancelled

        try
        {
            writeToPath(newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _windowService.ShowErrorAsync(
                "Save Failed", $"Could not save '{Path.GetFileName(newPath)}':\n{ex.Message}");
            return;
        }

        if (vm.FilePath is { } oldPath) _openTabs.Remove(oldPath);
        vm.FilePath = newPath;
        vm.IsDirty = false;
        _openTabs[newPath] = vm;
        RefreshNodes();
    }

    // Tab context menu's "Duplicate" — copies the tab's CURRENT (possibly unsaved) content to a new,
    // free filename next to the original, then opens it as its own tab. Only meaningful for a tab
    // that already has a home on disk (see EditorTabItemViewModel.HasFilePath, which gates the menu
    // item itself) — an untitled tab has no "next to" to duplicate into.
    public void DuplicateTab(EditorTabItemViewModel vm, Action<string> writeToPath)
    {
        if (vm.FilePath is not { } sourcePath) return;

        var dir = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);

        string newPath;
        var i = 1;
        do
        {
            var suffix = i == 1 ? " copy" : $" copy {i}";
            newPath = Path.Combine(dir, $"{baseName}{suffix}{ext}");
            i++;
        } while (File.Exists(newPath));

        try
        {
            writeToPath(newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _windowService.ShowErrorAsync(
                "Duplicate Failed", $"Could not create '{Path.GetFileName(newPath)}':\n{ex.Message}");
            return;
        }

        RefreshNodes();
        OpenTab(newPath);
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

    // Tabs for files living inside filePath — used when filePath is a FOLDER being deleted/renamed,
    // so files nested inside it aren't left with a tab open on a path that's about to disappear out
    // from under them (File.Delete/File.Move only ever touch a single file, not a whole subtree, so
    // this has no equivalent in the plain-file case below).
    private List<KeyValuePair<string, EditorTabItemViewModel>> FindOpenTabsUnder(string folderPath)
    {
        var prefix = folderPath + Path.DirectorySeparatorChar;
        return _openTabs.Where(kv => kv.Key.StartsWith(prefix, FilePathComparison)).ToList();
    }

    public void DeleteFile(string filePath)
    {
        // Directory.Exists/File.Delete are NOT interchangeable — File.Delete on a directory path
        // throws (silently swallowed below, before this fix), so a folder never actually left disk
        // even though the tree acted like it had (ANALYSIS.md TODO #69 follow-up).
        var isDirectory = Directory.Exists(filePath);

        // Close tab(s) first so SaveIfNeeded writes final content before the path disappears —
        // the file itself, or (for a folder) every tab nested inside it. Tracked before closing
        // (rather than just checked in the catch) so a failed delete can reopen them, same as
        // RenameFile — otherwise a failed delete left the tab(s) closed even though the file/folder
        // was still on disk (code-review finding on the #69/#72-era PR).
        var hadTab = _openTabs.ContainsKey(filePath);
        var nestedTabs = isDirectory ? FindOpenTabsUnder(filePath) : [];
        if (_openTabs.TryGetValue(filePath, out var tab))
            CloseTab(tab);
        foreach (var (_, nestedTab) in nestedTabs)
            CloseTab(nestedTab);

        try
        {
            if (isDirectory)
                Directory.Delete(filePath, recursive: true);
            else
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            // The tab(s) were already closed above — since the delete didn't happen, the file(s) are
            // still at the same path(s), so reopen them instead of leaving their tabs silently gone
            // from the tab bar. Report instead of the previous silent catch-and-ignore — a failed
            // delete used to leave the tree acting like the folder/file was gone while it was still
            // on disk, with no way to tell why (ANALYSIS.md TODO #69 follow-up).
            if (hadTab) OpenTab(filePath);
            foreach (var (nestedPath, _) in nestedTabs)
                OpenTab(nestedPath);
            _ = _windowService.ShowErrorAsync(
                "Delete Failed", $"Could not delete '{Path.GetFileName(filePath)}':\n{ex.Message}");
            return;
        }

        FileTree.RemoveByPath(filePath);
    }

    public void RenameFile(string filePath, string newName)
    {
        // Directory.Exists/File.Move are NOT interchangeable — File.Move on a directory path throws,
        // so renaming a folder always failed before this fix (ANALYSIS.md TODO #69 follow-up).
        // A folder's own name has no extension to preserve (unlike a .rpad file's).
        var isDirectory = Directory.Exists(filePath);
        var ext = isDirectory ? "" : Path.GetExtension(filePath);
        var dir = Path.GetDirectoryName(filePath)!;
        var newFileName = newName + ext;
        var newPath = Path.Combine(dir, newFileName);

        // Close tab(s) first so SaveIfNeeded writes current content to the OLD path — the file
        // itself, or (for a folder) every tab nested inside it, before the whole subtree moves.
        var hadTab = _openTabs.ContainsKey(filePath);
        var nestedTabs = isDirectory ? FindOpenTabsUnder(filePath) : [];
        if (_openTabs.TryGetValue(filePath, out var tab))
            CloseTab(tab);
        foreach (var (_, nestedTab) in nestedTabs)
            CloseTab(nestedTab);

        try
        {
            if (isDirectory)
                Directory.Move(filePath, newPath);
            else
                File.Move(filePath, newPath);
        }
        catch (Exception ex)
        {
            // The tab(s) were already closed above (so SaveIfNeeded could flush to the old path) —
            // since the rename didn't happen, the file(s) are still at the old path(s), so reopen
            // them instead of leaving their tabs silently gone from the tab bar.
            if (hadTab) OpenTab(filePath);
            foreach (var (nestedPath, _) in nestedTabs)
                OpenTab(nestedPath);
            _ = _windowService.ShowErrorAsync(
                "Rename Failed",
                $"Could not rename '{Path.GetFileName(filePath)}' to '{newFileName}':\n{ex.Message}");
            return;
        }

        RefreshNodes();

        if (hadTab)
            OpenTab(newPath);
        foreach (var (nestedPath, _) in nestedTabs)
            OpenTab(Path.Combine(newPath, Path.GetRelativePath(filePath, nestedPath)));
    }

    // TODO.md #69: creates a new folder next to whatever's selected in the file tree — INSIDE the
    // selected node if it's itself a folder (matching VS Code/Rider), alongside it (same parent
    // directory) if it's a file, or at the tree's root if nothing's selected. Returns the new path so
    // the caller (MainWindow.FileTree_CreateFolder) can immediately enter rename mode on it, like
    // VS/Rider/Explorer do.
    public string CreateFolder(string? contextPath)
    {
        var parentDir = contextPath is null
            ? _fileSystemService.Folder
            : Directory.Exists(contextPath)
                ? contextPath
                : Path.GetDirectoryName(contextPath) ?? _fileSystemService.Folder;

        var path = _fileSystemService.CreateNewFolder(parentDir);
        RefreshNodes();
        return path;
    }

    public void OpenContainingFolder(string filePath)
    {
        try
        {
            _fileSystemService.OpenFolder(filePath);
        }
        catch (Exception ex)
        {
            _ = _windowService.ShowErrorAsync("Could Not Open File Manager", ex.Message);
        }
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    public FileTreeViewModel FileTree { get; }

    public List<DotnetSdk> AvailableSdks { get; }

    public MainWindowViewModel(
        IEnumerable<ISdkProvider> sdkProviders,
        IFileSystemService fileSystemService,
        IWindowService windowService,
        IPreferencesService prefs,
        ILanguageHostRegistry languageRegistry,
        // Optional with a null-object fallback (rather than a required DI parameter) so the many
        // existing tests constructing this type directly don't all need updating just to supply a
        // logger they don't care about — real app startup (App.axaml.cs's AddTransient) still gets
        // a real one, since DI resolves ILogger<MainWindowViewModel> from the registered logging
        // provider regardless of the parameter having a default (ANALYSIS.md #94).
        ILogger<MainWindowViewModel>? logger = null)
    {
        _fileSystemService = fileSystemService;
        _windowService = windowService;
        _prefs = prefs;
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        // TODO.md #80/#81: FileTreeViewModel now needs IPreferencesService (RenameGesture/
        // DeleteGesture) and ILanguageHostRegistry (per-language file icon), so this moves from a
        // property initializer (which would run before _prefs is assigned above) into the
        // constructor body.
        FileTree = new FileTreeViewModel(_prefs, languageRegistry);
        OpenDependenciesCommand = ReactiveCommand.CreateFromTask(OpenDependenciesWindowAsync);
        OpenPreferencesCommand = ReactiveCommand.CreateFromTask(_windowService.ShowPreferencesAsync);
        OpenAboutCommand = ReactiveCommand.CreateFromTask(_windowService.ShowAboutAsync);
        // Was silently swallowed (ANALYSIS.md #94) — a failure opening one of these dialogs used to
        // vanish with no trace at all.
        OpenDependenciesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenDependenciesCommand failed"));
        OpenPreferencesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenPreferencesCommand failed"));
        OpenAboutCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenAboutCommand failed"));
        AvailableSdks = sdkProviders.SelectMany(p => p.Get()).ToList();

        SidebarColumnWidth = _prefs.Layout.SidebarCollapsed
            ? new GridLength(0)
            : new GridLength(_prefs.Layout.SidebarWidth);
        _lastExpandedSidebarWidth = _prefs.Layout.SidebarWidth;
        ToggleSidebarCommand = ReactiveCommand.Create(ToggleSidebar);
        this.WhenAnyValue(x => x.SidebarColumnWidth)
            .Skip(1)
            .Throttle(LayoutSaveThrottle, RxApp.MainThreadScheduler)
            .Subscribe(w =>
            {
                _prefs.Layout.SidebarCollapsed = w.Value <= 0;
                if (w.Value > 0) _prefs.Layout.SidebarWidth = w.Value;
                _prefs.Save();
            });

        RefreshNodes();

        _fileSystemService.WatcherEvent += OnWatcherEvent;
        _fileSystemService.StartWatcher();
    }

    // ── Watcher ───────────────────────────────────────────────────────────────

    private void OnWatcherEvent(object? sender, FileSystemEventArgs e)
    {
        // Version-based debounce: capture current version, wait for WatcherRefreshDebounce,
        // then refresh only if no newer event arrived.
        var version = Interlocked.Increment(ref _debounceVersion);
        Task.Delay(WatcherRefreshDebounce).ContinueWith(_ =>
        {
            if (_debounceVersion == version)
                Dispatcher.UIThread.Post(RefreshNodes);
        });
    }

    private void RefreshNodes() => FileTree.Refresh(_fileSystemService.GetFilesTree());

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _fileSystemService.WatcherEvent -= OnWatcherEvent;
        _fileSystemService.StopWatcher();
        foreach (var d in _closeSubscriptions.Values)
        {
            d.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
