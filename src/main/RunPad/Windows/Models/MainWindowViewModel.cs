using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Shared;
using RunPad.UserControls.Models;

namespace RunPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly IWindowService _windowService;
    private readonly IPreferencesService _prefs;
    private readonly ILogger<MainWindowViewModel> _logger;

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
    public ReactiveCommand<Unit, Unit> ImportLinqFileCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAssemblyExplorerCommand { get; }

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

    // Feeds MainWindow.axaml's File > New File MenuItem.InputGesture (TODO.md #114) — same rationale
    // as OpenDependenciesGesture above.
    public KeyGesture? NewFileGesture
    {
        get
        {
            try { return KeyGesture.Parse(_prefs.Keyboard.NewFile); }
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

    // TODO.md #103: was its own copy of the collapse/expand-plus-throttled-persist scheme,
    // duplicated near-verbatim in EditorTabItemViewModel.OutputPanelHeight/ToggleOutputPanel —
    // extracted into CollapsiblePanelState (src/main/RunPad/Shared), constructed below.
    public CollapsiblePanelState Sidebar { get; }

    // ── Tab collection ────────────────────────────────────────────────────────

    public ObservableCollection<EditorTabItemViewModel> Tabs { get; } = [];

    private EditorTabItemViewModel? _activeTab;

    public EditorTabItemViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            PersistSession();
        }
    }

    // Remembers which files are open (and in what order) plus which one is active, so the next
    // launch can restore them — see RestoreSession. Untitled tabs are skipped: they were never on
    // disk, so there's no path to remember them by (TODO.md, "Жизненный цикл вкладок (остаток)").
    // Called after every tab open/close/switch — a plain local JSON write on a low-frequency user
    // action, not worth the debounce Sidebar's own drag-frame writes need.
    private void PersistSession()
    {
        _prefs.Session.OpenTabs = Tabs.Where(t => t.FilePath is not null).Select(t => t.FilePath!).ToList();
        _prefs.Session.ActiveTabPath = ActiveTab?.FilePath;
        _prefs.Save();
    }

    // Replaces the old unconditional "open one blank tab" at startup (EditorTabsControl.Attach) —
    // restores every previously-open, still-on-disk tab in its saved order, then re-selects whichever
    // one was active. Falls back to a single blank tab when there's nothing to restore (first run, or
    // every saved path has since been deleted/moved).
    public void RestoreSession()
    {
        // Captured before the loop below, not re-read after it: each OpenTab call inside the loop
        // makes its own tab the active one, which fires PersistSession (via the ActiveTab setter) and
        // overwrites _prefs.Session.ActiveTabPath with whatever was JUST opened — reading the property
        // again afterward would always see the last-restored tab instead of the one actually active
        // when the session was saved.
        var savedActivePath = _prefs.Session.ActiveTabPath;
        var savedOpenTabs = _prefs.Session.OpenTabs.ToList();

        var restoredAny = false;
        foreach (var path in savedOpenTabs)
        {
            if (!File.Exists(path)) continue;
            OpenTab(path);
            restoredAny = true;
        }

        if (!restoredAny)
        {
            OpenTab(null);
            return;
        }

        if (savedActivePath is { } activePath && _openTabs.TryGetValue(activePath, out var activeVm))
            ActiveTab = activeVm;
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

    // TODO.md #114: Ctrl+N / the new File > New File menu item — same as NewRpadFile above, but reads
    // the language+kind to use from EditorSettings instead of requiring an explicit choice, so it
    // never has to show NewFileKindDialog. The explicit File > New RPad File submenu (still asking via
    // the dialog) and the file tree's own "New File" (TODO.md #109, writes to disk immediately) are
    // both untouched — this only adds a third, dialog-free entry point.
    public void NewRpadFileWithDefaults() => NewRpadFile(_prefs.Editor.NewFileLanguage, _prefs.Editor.NewFileKind);

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
        // CloseCommand is deliberately NOT subscribed here — unlike OpenContainingFolderCommand, it
        // needs EditorsTabItemControl.WriteFile (to save an untitled-but-dirty tab's content when the
        // user picks "Save" from the confirmation dialog), which only the View layer has. See
        // EditorTabsControl.RequestCloseTabAsync, subscribed there instead.
        var subs = new CompositeDisposable
        {
            vm.OpenContainingFolderCommand.Subscribe(_ =>
            {
                if (vm.FilePath is { } path) OpenContainingFolder(path);
            }),
        };
        _closeSubscriptions[vm] = subs;

        Tabs.Add(vm);
        ActiveTab = vm; // also persists the session (see the setter) — covers the Tabs.Add above too
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
        // Neither Tabs nor ActiveTab changed (same vm, same position) but the path list did —
        // PersistSession's usual triggers (the ActiveTab setter, CloseTab) won't fire here.
        PersistSession();
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

    // "File → Import LINQPad Query..." (TODO.md #63) — converts a .linq file (picked via a native
    // Open dialog, so it can live anywhere on disk, not just inside the current workspace folder) to
    // a sibling .rpad next to it, then opens that as a real tab. Not written into the CURRENT
    // workspace folder even if the .linq happens to live elsewhere — mirrors DuplicateTab's own
    // "next to the original" convention rather than always landing inside whatever workspace happens
    // to be open; if that means the new file isn't under the tracked folder, RefreshNodes below just
    // won't show it in the sidebar (the tab itself still opens and works fine either way).
    public async Task ImportLinqFileAsync()
    {
        var linqPath = await _windowService.ShowOpenLinqFileDialogAsync(_fileSystemService.Folder);
        if (linqPath is null) return;

        RpadDocument converted;
        try
        {
            var linqText = await File.ReadAllTextAsync(linqPath);
            converted = LinqConverter.Convert(linqText, title: Path.GetFileNameWithoutExtension(linqPath));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await _windowService.ShowErrorAsync(
                "Import Failed", $"Could not import '{Path.GetFileName(linqPath)}':\n{ex.Message}");
            return;
        }

        var dir = Path.GetDirectoryName(linqPath)!;
        var baseName = Path.GetFileNameWithoutExtension(linqPath);

        string newPath;
        var i = 1;
        do
        {
            var suffix = i == 1 ? "" : $" {i}";
            newPath = Path.Combine(dir, $"{baseName}{suffix}.rpad");
            i++;
        } while (File.Exists(newPath));

        try
        {
            await File.WriteAllTextAsync(newPath, RpadWriter.Write(converted));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _windowService.ShowErrorAsync(
                "Import Failed", $"Could not create '{Path.GetFileName(newPath)}':\n{ex.Message}");
            return;
        }

        RefreshNodes();
        OpenTab(newPath);
    }

    // Asks whether to save an untitled-but-dirty tab's content before its close discards it — see
    // EditorTabsControl.RequestCloseTabAsync, the sole caller (both the tab's [x] button and Ctrl+W
    // route through it). A thin passthrough to _windowService, which EditorTabsControl doesn't have
    // its own reference to.
    public Task<UnsavedChangesResult> ConfirmDiscardUnsavedChangesAsync(string fileName)
        => _windowService.ShowUnsavedChangesDialogAsync(fileName);

    // internal (not private): EditorTabsControl.RequestCloseTabAsync calls this directly once the
    // close is actually confirmed (or didn't need confirming) — the View-layer orchestration around
    // it (the confirmation dialog, needing ctrl.WriteFile for "Save") can't live here, see AddTab's
    // comment on why CloseCommand itself isn't subscribed in this class.
    internal void CloseTab(EditorTabItemViewModel vm)
    {
        if (_closeSubscriptions.TryGetValue(vm, out var d))
        {
            d.Dispose();
            _closeSubscriptions.Remove(vm);
        }

        if (vm.FilePath is not null) _openTabs.Remove(vm.FilePath);

        bool wasActive = ActiveTab == vm;
        Tabs.Remove(vm);
        if (wasActive)
            ActiveTab = Tabs.LastOrDefault(); // also persists the session (see the setter)
        else
            PersistSession(); // ActiveTab unchanged, so its setter won't fire — persist explicitly
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

        MoveOrRenameOnDisk(filePath, newPath, "Rename Failed", "rename", $"to '{newFileName}'");
    }

    // TODO.md #110: file tree drag-and-drop — moves a node into a different folder (or the tree's
    // root, contextPath null), reusing the exact same target-directory resolution CreateFolderAsync/
    // CreateFileAsync already use (ResolveParentDir) — dropping onto a folder node moves INSIDE it,
    // onto a file node moves alongside it (same parent), onto empty tree space moves to the root. A
    // no-op (not an error) if that resolves to the node's own current parent — that's what dropping a
    // node back onto where it already lives looks like; dropping a folder onto its own descendant is
    // still a real error (Directory.Move already throws a sensible message for that on its own, no
    // separate check needed).
    public void MoveFileOrFolder(string sourcePath, string? contextPath)
    {
        var targetDir = ResolveParentDir(contextPath);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var newPath = Path.Combine(targetDir, name);

        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(sourcePath), StringComparison.Ordinal))
            return;

        MoveOrRenameOnDisk(sourcePath, newPath, "Move Failed", "move", $"into '{Path.GetFileName(targetDir)}'");
    }

    // Shared by RenameFile (same directory, new name) and MoveFileOrFolder (new directory, same
    // name) — both close whatever tab(s) are open on the affected path(s) first (so SaveIfNeeded
    // flushes to the OLD path before the move), attempt the actual File.Move/Directory.Move, and
    // reopen at the old or new path depending on whether it succeeded.
    private void MoveOrRenameOnDisk(
        string sourcePath, string newPath, string errorTitle, string errorVerb, string errorDestinationPhrase)
    {
        var isDirectory = Directory.Exists(sourcePath);

        var hadTab = _openTabs.ContainsKey(sourcePath);
        var nestedTabs = isDirectory ? FindOpenTabsUnder(sourcePath) : [];
        if (_openTabs.TryGetValue(sourcePath, out var tab))
            CloseTab(tab);
        foreach (var (_, nestedTab) in nestedTabs)
            CloseTab(nestedTab);

        try
        {
            if (isDirectory)
                Directory.Move(sourcePath, newPath);
            else
                File.Move(sourcePath, newPath);
        }
        catch (Exception ex)
        {
            // The tab(s) were already closed above (so SaveIfNeeded could flush to the old path) —
            // since the move didn't happen, the file(s) are still at the old path(s), so reopen them
            // instead of leaving their tabs silently gone from the tab bar.
            if (hadTab) OpenTab(sourcePath);
            foreach (var (nestedPath, _) in nestedTabs)
                OpenTab(nestedPath);
            _ = _windowService.ShowErrorAsync(
                errorTitle,
                $"Could not {errorVerb} '{Path.GetFileName(sourcePath)}' {errorDestinationPhrase}:\n{ex.Message}");
            return;
        }

        RefreshNodes();

        if (hadTab)
            OpenTab(newPath);
        foreach (var (nestedPath, _) in nestedTabs)
            OpenTab(Path.Combine(newPath, Path.GetRelativePath(sourcePath, nestedPath)));
    }

    // TODO.md #69: creates a new folder next to whatever's selected in the file tree — INSIDE the
    // selected node if it's itself a folder (matching VS Code/Rider), alongside it (same parent
    // directory) if it's a file, or at the tree's root if nothing's selected. Returns the new path so
    // the caller (MainWindow.FileTree_CreateFolder) can immediately enter rename mode on it, like
    // VS/Rider/Explorer do.
    public async Task<string> CreateFolderAsync(string? contextPath)
    {
        var parentDir = ResolveParentDir(contextPath);
        var path = _fileSystemService.CreateNewFolder(parentDir);
        await RefreshNodesAsync();
        return path;
    }

    // TODO.md #109: file tree's "New File" — same target-directory resolution as CreateFolderAsync
    // (reused via ResolveParentDir below), but unlike "New Folder" the new file is also immediately
    // useful to open, so it's opened as a tab right away (same OpenTab used for double-click) rather
    // than left for the user to click on themselves.
    public async Task<string> CreateFileAsync(string? contextPath, RpadLanguage language, RpadKind kind)
    {
        var parentDir = ResolveParentDir(contextPath);
        var path = _fileSystemService.CreateNewFile(parentDir, language, kind);
        await RefreshNodesAsync();
        OpenTab(path);
        return path;
    }

    private string ResolveParentDir(string? contextPath) =>
        contextPath is null
            ? _fileSystemService.Folder
            : Directory.Exists(contextPath)
                ? contextPath
                : Path.GetDirectoryName(contextPath) ?? _fileSystemService.Folder;

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
        ImportLinqFileCommand = ReactiveCommand.CreateFromTask(ImportLinqFileAsync);
        OpenAssemblyExplorerCommand = ReactiveCommand.CreateFromTask(
            () => _windowService.ShowAssemblyExplorerAsync(_fileSystemService.Folder));
        // Was silently swallowed (ANALYSIS.md #94) — a failure opening one of these dialogs used to
        // vanish with no trace at all.
        OpenDependenciesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenDependenciesCommand failed"));
        OpenPreferencesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenPreferencesCommand failed"));
        OpenAboutCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenAboutCommand failed"));
        ImportLinqFileCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "ImportLinqFileCommand failed"));
        OpenAssemblyExplorerCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenAssemblyExplorerCommand failed"));
        AvailableSdks = sdkProviders.SelectMany(p => p.Get(prefs.Debugger.DotnetPath)).ToList();

        Sidebar = new CollapsiblePanelState(
            _prefs.Layout.SidebarCollapsed, _prefs.Layout.SidebarWidth, defaultExpandedSize: 300,
            onChanged: (collapsed, size) =>
            {
                _prefs.Layout.SidebarCollapsed = collapsed;
                if (size > 0) _prefs.Layout.SidebarWidth = size;
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

    // ANALYSIS.md #104: fire-and-forget wrapper for every call site that doesn't need to know when
    // the refresh actually lands (the constructor, the watcher above, every disk-mutating command
    // except CreateFolderAsync) — GetFilesTreeAsync's own Task.Run means the disk walk never blocks
    // whichever thread called this, only FileTree.Refresh's (already cheap, diff-based) merge runs
    // back on the UI thread once it resolves.
    private async void RefreshNodes() => await RefreshNodesAsync();

    // CreateFolderAsync is the one caller that needs to actually await this — FileTree_CreateFolder
    // (MainWindow.axaml.cs) calls FileTreeView.StartRenameForPath immediately after, which needs the
    // new folder to already be in FileTree.Nodes to find it.
    private async Task RefreshNodesAsync() => FileTree.Refresh(await _fileSystemService.GetFilesTreeAsync());

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
