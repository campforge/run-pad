using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Examples;
using RunPad.Services.Preferences;
using RunPad.Services.Recovery;
using RunPad.Shared;
using RunPad.UserControls.Models;

namespace RunPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable, IEditorTabActions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly IWindowService _windowService;
    private readonly IPreferencesService _prefs;
    private readonly ILanguageHostRegistry _languageRegistry;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IRecoveryStore? _recovery;
    private readonly bool _recoveryMode;
    private readonly SemaphoreSlim _recoveryGate = new(1);
    private Task? _restoreTask;
    private long _recoveryRevision;
    private bool _recoveryFailed;
    private readonly HashSet<Task> _workspaceOperations = [];

    private Task TrackWorkspaceOperation(Func<Task> operation)
    {
        _workspaceOperations.RemoveWhere(task => task.IsCompleted);
        var task = operation();
        if (!task.IsCompleted) _workspaceOperations.Add(task);
        return task;
    }

    public bool RestoresDrafts => _recovery is not null && _recoveryMode && !_recoveryFailed;

    public Task RestoreSessionAsync() => _restoreTask ??= RestoreDraftsAsync();

    public bool IsRestoringWorkspace
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private async Task RestoreDraftsAsync()
    {
        IsRestoringWorkspace = true;
        try { await RestoreDraftsCoreAsync(); }
        finally { IsRestoringWorkspace = false; }
    }

    private async Task RestoreDraftsCoreAsync()
    {
        if (!RestoresDrafts) { RestoreSession(); return; }
        try
        {
            var snapshot = await _recovery!.LoadAsync();
            if (_disposed) return;
            var imports = _recovery.Imports ?? [];
            if (snapshot is null)
            {
                RestoreSession();
                AddImportedDrafts(imports);
                RecoveryNotice = JoinNotice(_importNotice, _recovery.Warning);
                return;
            }
            _recoveryRevision = snapshot.Revision;
            var frames = new List<RecoveryTab>();
            foreach (var tab in snapshot.Tabs)
            {
                var frame = tab;
                if (!tab.IsDirty && tab.FilePath is { } path)
                {
                    try
                    {
                        var bytes = await Task.Run(() => File.ReadAllBytes(path));
                        string text;
                        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)) text = reader.ReadToEnd();
                        var parsed = RpadParser.Parse(text);
                        frame = tab with
                        {
                            EditorText = parsed.Document?.RunCode ?? text,
                            Document = parsed.Document?.CaptureSnapshot(),
                            ParseError = parsed.Error,
                            BaselineKnown = true,
                            DiskHash = ScriptFileState.Of(bytes).Hash,
                            MetadataBaseline = null,
                        };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        frame = tab with { IsDirty = true };
                    }
                }
                else if (tab.FilePath is null) frame = tab with { IsDirty = true };
                frames.Add(frame);
            }
            if (_disposed) return;
            if (frames.Where(t => t.FilePath is not null).Select(t => t.FilePath!).Distinct(FilePathComparer).Count()
                != frames.Count(t => t.FilePath is not null)) throw new InvalidDataException("Duplicate recovery paths.");
            _restoringSession = true;
            try
            {
                foreach (var frame in frames)
                {
                    var vm = new EditorTabItemViewModel(frame.FilePath, AvailableSdks, _prefs, this,
                        frame.Document?.FrontMatter.Language ?? RpadLanguage.CSharp,
                        frame.Document?.FrontMatter.Kind ?? RpadKind.Statements)
                    {
                        RecoveryId = frame.Id,
                        RestoredRecovery = frame,
                    };
                    if (frame.FilePath is not null) _openTabs[frame.FilePath] = vm;
                    AddTab(vm);
                }
                ActiveTab = Tabs.FirstOrDefault(t => t.RecoveryId == snapshot.ActiveTab) ?? Tabs.FirstOrDefault();
            }
            finally { _restoringSession = false; }
            AddImportedDrafts(imports);
            PersistSession();
            RecoveryNotice = JoinNotice(_importNotice, _recovery.Warning);
        }
        catch (Exception ex)
        {
            _recoveryFailed = true;
            _logger.LogWarning("Could not restore drafts ({ErrorType})", ex.GetType().Name);
            RecoveryNotice = "Saved drafts could not be read and have been kept unchanged. File-backed session restoration and normal save confirmations will be used."
                + (_recovery?.Warning is { } details ? "\n" + details : "");
            if (!_disposed && Tabs.Count == 0) RestoreSession();
        }
    }

    /// <summary>A non-modal startup notice about drafts: what was imported, and what could not be
    /// read and was kept where it is. Null when there is nothing to say.</summary>
    public string? RecoveryNotice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public void DismissRecoveryNotice() => RecoveryNotice = null;

    // Unsaved drafts of RunPad windows that closed while this one was not running. A draft of a file
    // already open here (or in another imported draft) becomes an untitled copy that names the file,
    // so neither version is overwritten; the others reopen their file, keeping the baseline they were
    // edited against, so a later save still asks about changes on disk. Nothing is checkpointed here:
    // the next checkpoint (closing a tab or the app) records the imports, and until then their
    // sessions stay locked and unchanged, so a crash only offers the same drafts again.
    private void AddImportedDrafts(IReadOnlyList<RecoveryImport> imports)
    {
        var drafts = imports.SelectMany(i => i.Tabs).ToList();
        if (drafts.Count == 0) return;
        var active = ActiveTab;
        var copies = 0;
        _restoringSession = true;
        try
        {
            foreach (var draft in drafts)
            {
                var frame = draft;
                if (draft.FilePath is { } path && _openTabs.ContainsKey(path))
                {
                    copies++;
                    frame = draft with
                    {
                        FilePath = null,
                        OriginalPath = path,
                        Title = $"{Path.GetFileNameWithoutExtension(path)} (from another window)",
                        BaselineKnown = false,
                        DiskHash = null,
                        IsDirty = true,
                    };
                }
                else if (draft.FilePath is null) frame = draft with { IsDirty = true };
                var vm = new EditorTabItemViewModel(frame.FilePath, AvailableSdks, _prefs, this,
                    frame.Document?.FrontMatter.Language ?? RpadLanguage.CSharp,
                    frame.Document?.FrontMatter.Kind ?? RpadKind.Statements)
                {
                    RecoveryId = frame.Id,
                    RestoredRecovery = frame,
                };
                if (frame.FilePath is not null) _openTabs[frame.FilePath] = vm;
                AddTab(vm);
            }
            if (active is not null) ActiveTab = active;
        }
        finally { _restoringSession = false; }
        var notice = drafts.Count == 1
            ? "Restored 1 unsaved draft from another RunPad window."
            : $"Restored {drafts.Count} unsaved drafts from other RunPad windows.";
        if (copies > 0)
            notice += copies == 1
                ? " Its file was already open here, so it is an untitled copy that names the file."
                : $" {copies} of them are untitled copies because their files were already open here.";
        _importNotice = notice;
    }

    private string? _importNotice;

    private static string? JoinNotice(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "\n" + second;

    /// <summary>
    /// Closing the app while draft recovery is on. Once the restore, the editors and any pending
    /// save or file operation have finished: without unsaved tabs the workspace is checkpointed;
    /// otherwise the user chooses Save All (the usual Save, Save As and changed-on-disk questions;
    /// any cancel or failure keeps the app open, without undoing saves already made), Keep in Stash
    /// (closes only once the drafts are durably checkpointed) or Cancel. True means the app may
    /// close; null that draft recovery turned out unavailable, so the normal confirmations apply.
    /// </summary>
    public async Task<bool?> ConfirmExitWithDraftsAsync()
    {
        if (_restoreTask is { } restoring) await restoring;
        if (!RestoresDrafts) return null;
        await _recoveryGate.WaitAsync();
        try
        {
            try { await WaitForPendingWorkAsync(null); }
            catch (Exception ex)
            {
                // Not knowing which tabs are unsaved must not close anything.
                _logger.LogWarning("Could not finish pending work before closing ({ErrorType})", ex.GetType().Name);
                await _windowService.ShowErrorAsync("Not Closed",
                    "A save or file operation did not finish. Nothing has been closed; try closing again.");
                return false;
            }
            var unsaved = Tabs.Where(t => t.IsDirty).ToList();
            if (unsaved.Count == 0) return await CheckpointRecoveryCoreAsync();
            switch (await _windowService.ShowExitUnsavedDialogAsync(unsaved.Select(t => t.FileName).ToList()))
            {
                case ExitUnsavedChoice.KeepInStash:
                    return await CheckpointRecoveryCoreAsync();
                case ExitUnsavedChoice.SaveAll:
                    foreach (var tab in unsaved)
                    {
                        if (!tab.IsDirty || !Tabs.Contains(tab)) continue;
                        if (tab.FilePath is not null)
                        {
                            var result = await SaveInPlaceAsync(tab);
                            if (result.Status == InPlaceSaveStatus.Cancelled) return false;
                            if (result.Status == InPlaceSaveStatus.Failed)
                            {
                                await _windowService.ShowErrorAsync("Save Failed", result.Error);
                                return false;
                            }
                        }
                        else
                        {
                            await ((IEditorTabActions)this).SaveAsAsync(tab);
                            if (tab.IsDirty) return false; // Save As was cancelled or failed
                        }
                    }
                    // The session (which files are open, the active tab) is still kept for the next start.
                    return await CheckpointRecoveryCoreAsync();
                default:
                    return false;
            }
        }
        finally { _recoveryGate.Release(); }
    }

    public async Task<bool> CheckpointRecoveryAsync()
    {
        if (_restoreTask is { } restoring) await restoring;
        if (!RestoresDrafts) return false;
        await _recoveryGate.WaitAsync();
        try { return await CheckpointRecoveryCoreAsync(); }
        finally { _recoveryGate.Release(); }
    }

    private async Task<bool> CheckpointRecoveryCoreAsync(EditorTabItemViewModel? exclude = null)
    {
        try
        {
            var tabs = await WaitForPendingWorkAsync(exclude);
            var frames = tabs.Select(t => t.CaptureRecovery?.Invoke()
                ?? throw new InvalidOperationException("Editor capture is unavailable.")).ToList();
            var active = frames.FirstOrDefault(t => t.Id == ActiveTab?.RecoveryId)?.Id ?? frames.LastOrDefault()?.Id;
            await _recovery!.CommitAsync(new RecoverySnapshot { Revision = ++_recoveryRevision, Tabs = frames, ActiveTab = active });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not checkpoint drafts ({ErrorType})", ex.GetType().Name);
            await _windowService.ShowErrorAsync("Drafts Not Saved",
                "Draft recovery could not be saved. Nothing has been closed. Check free space and permissions, then close again to retry, or save your files manually.");
            return false;
        }
    }


    // The tabs once every editor exists and no save, Save As or workspace operation is running.
    private async Task<List<EditorTabItemViewModel>> WaitForPendingWorkAsync(EditorTabItemViewModel? exclude)
    {
        var tabs = Tabs.Where(t => t != exclude).ToList();
        // A closing window must not skip tabs whose per-language editor is still being created.
        await Task.WhenAll(tabs.Select(t => t.RecoveryReady.Task)).WaitAsync(TimeSpan.FromSeconds(20));
        // Save As owns path rebasing as well as the write; a snapshot must see both or neither.
        while (true)
        {
            var pending = Tabs.SelectMany(t => new Task?[] { t.PendingSave, t.PendingSaveAs }).OfType<Task>()
                .Concat(_workspaceOperations).Where(task => !task.IsCompleted).Distinct().ToArray();
            if (pending.Length == 0) break;
            await Task.WhenAll(pending);
        }
        _workspaceOperations.RemoveWhere(task => task.IsCompleted);
        tabs = Tabs.Where(t => t != exclude).ToList();
        await Task.WhenAll(tabs.Select(t => t.RecoveryReady.Task)).WaitAsync(TimeSpan.FromSeconds(20));
        return tabs;
    }

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

    // Debounce state for watcher-triggered refreshes
    private volatile int _debounceVersion;

    // The UI dispatcher, captured at construction for the watcher debounce's thread-pool continuation
    // (same target as before). Reading the static Dispatcher.UIThread from that continuation instead
    // would, after Avalonia.Headless resets it between isolated tests, make a thread-pool thread
    // "the UI thread" and fail the next test's app setup (VerifyAccess).
    private readonly Dispatcher _dispatcher = Dispatcher.UIThread;

    // File-tree refresh ordering/lifetime. Each walk gets a version; only the result of
    // the LATEST requested walk is applied (or reported) — an older walk finishing late can't
    // overwrite a newer tree, nor resurrect a stale one after a newer walk failed and deliberately
    // kept the previous tree. _treeErrorReported keeps a persistently unreadable root from re-showing the same error
    // on every watcher event — reset by the next successful refresh.
    private int _refreshVersion;
    private bool _treeErrorReported;
    private bool _disposed;
    private readonly CancellationTokenSource _disposeCts = new();

    private bool _restoringSession;
    private bool _openingFile;
    public event Action? RecentFilesChanged;
    public IReadOnlyList<string> RecentFiles => _prefs.Session.RecentFiles;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public ReactiveCommand<Unit, Unit> OpenFileCommand { get; }
    public ReactiveCommand<string, Unit> OpenRecentFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearRecentFilesCommand { get; }

    private Task OpenFileAsync() => TrackWorkspaceOperation(OpenFileCoreAsync);

    private async Task OpenFileCoreAsync()
    {
        if (_disposed || _openingFile) return;
        _openingFile = true;
        try
        {
            var folder = ActiveTab?.FilePath is { } current ? Path.GetDirectoryName(current)! : _fileSystemService.Folder;
            var path = await _windowService.ShowOpenScriptFileDialogAsync(folder);
            if (path is not null) await OpenScriptAsync(path);
        }
        finally { _openingFile = false; }
    }

    internal async Task OpenScriptAsync(string path)
    {
        if (_restoreTask is { IsCompleted: false } restoring) await restoring;
        if (_disposed) return;
        try
        {
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), ".rpad", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a .rpad script.");
            // An already-open dirty tab remains usable even if its file was removed elsewhere.
            if (!_openTabs.ContainsKey(path))
            {
                using var readable = File.OpenRead(path);
            }
            OpenTab(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await _windowService.ShowErrorAsync("Open Failed", $"Could not open '{path}':\n{ex.Message}");
        }
    }

    private void RememberRecentFile(string path)
    {
        if (_restoringSession) return;
        _prefs.Session.RecentFiles = RecentFiles.Where(p => !FilePathComparer.Equals(p, path)).Prepend(path).Take(50).ToList();
        RecentFilesUpdated();
    }

    private void SaveSessionSettings()
    {
        try { _prefs.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save session and recent files");
        }
    }

    private void RecentFilesUpdated()
    {
        SaveSessionSettings();
        this.RaisePropertyChanged(nameof(HasRecentFiles));
        RecentFilesChanged?.Invoke();
    }

    // ── Window commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> OpenDependenciesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAboutCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSecretStoreCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenConnectionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportLinqFileCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAssemblyExplorerCommand { get; }

    /// <summary>File → "Open Text File...": any file in its own text editor window.</summary>
    public ReactiveCommand<Unit, Unit> OpenTextFileCommand { get; }

    private async Task OpenTextFileAsync()
    {
        var path = await _windowService.ShowOpenTextFileDialogAsync(_fileSystemService.Folder);
        if (path is not null) await _windowService.ShowTextEditorAsync(path);
    }

    // Raised after the Dependencies dialog closes, so the view can refresh IntelliSense for the
    // (possibly package-mutated) active tab. RpadFrontMatter has no change notification of its own.
    public event Action? DependenciesWindowClosed;

    /// <summary>A script or folder in the scripts folder changed on disk (raised on the UI thread).</summary>
    public event Action? ScriptsFolderChanged;

    public void OpenDependenciesWindow() => _ = OpenDependenciesWindowAsync();

    private async Task OpenDependenciesWindowAsync()
    {
        await _windowService.ShowDependenciesAsync(ActiveTab?.WhenAnyValue(x => x.FrontMatter), ActiveTab?.WhenAnyValue(x => x.FilePath));
        DependenciesWindowClosed?.Invoke();
        // Dependencies can edit the reusable connections too.
        RefreshConnectionNavigation();
    }

    // ── Sidebar: Connections bundle ─────────────────────────────────────────────

    /// <summary>The sidebar's Connections tree; null when RunPad was started without a connection store.</summary>
    public ConnectionNavigationViewModel? ConnectionNavigation { get; }

    public bool HasConnectionNavigation => ConnectionNavigation is not null;

    /// <summary>Opens the Connections editor (at the named connection), then shows what it saved in the tree.</summary>
    public async Task OpenConnectionsAsync(string? connectionName = null)
    {
        await _windowService.ShowConnectionsAsync(connectionName);
        RefreshConnectionNavigation();
    }

    /// <summary>
    /// Adds a saved connection to the active script right away, asking first only when its driver
    /// needs NuGet packages the script doesn't have; the tab then takes the change like an edit made
    /// in Dependencies (marked changed, IntelliSense refreshed). Why it couldn't be added shows in the tree.
    /// </summary>
    public async Task AddConnectionToScriptAsync(string connectionName)
    {
        if (ConnectionNavigation is not { } navigation || ActiveTab is not { FrontMatter: { } frontMatter } tab) return;
        if (await navigation.AddToScriptAsync(frontMatter, tab.FilePath, connectionName, _windowService.ConfirmConnectionPackagesAsync))
            DependenciesWindowClosed?.Invoke();
    }

    // Reading happens off the UI thread; an unexpected failure is logged, the tree keeps what it showed.
    private void RefreshConnectionNavigation()
    {
        if (ConnectionNavigation is not { } navigation) return;
        _ = navigation.RefreshAsync().ContinueWith(
            t => _logger.LogError(t.Exception, "Refreshing the connections tree failed"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // ── Sidebar: sections and the Samples tree ──────────────────────────────────

    public const string ConnectionsSection = "Connections";
    public const string ScriptsSection = "Scripts";
    public const string SamplesSection = "Samples";

    // Which sections are open is kept in the layout settings, so the sidebar looks the same next time.
    public bool IsConnectionsSectionExpanded
    {
        get => IsSectionExpanded(ConnectionsSection);
        set => SetSectionExpanded(ConnectionsSection, value);
    }

    public bool IsScriptsSectionExpanded
    {
        get => IsSectionExpanded(ScriptsSection);
        set => SetSectionExpanded(ScriptsSection, value);
    }

    public bool IsSamplesSectionExpanded
    {
        get => IsSectionExpanded(SamplesSection);
        set => SetSectionExpanded(SamplesSection, value);
    }

    private bool IsSectionExpanded(string section) => !(_prefs.Layout.CollapsedSidebarSections ?? []).Contains(section);

    private void SetSectionExpanded(string section, bool expanded, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (IsSectionExpanded(section) == expanded) return;
        var collapsed = (_prefs.Layout.CollapsedSidebarSections ?? []).Where(s => s != section).ToList();
        if (!expanded) collapsed.Add(section);
        _prefs.Layout.CollapsedSidebarSections = collapsed;
        _prefs.Save();
        this.RaisePropertyChanged(property);
    }

    public bool ShowNoExamplesHint => Examples.Count == 0;

    /// <summary>The samples in their folders; folded folders are kept in the layout settings.</summary>
    public IReadOnlyList<SampleTreeNode> SampleTree => field ??= SampleTreeNode.Build(
        Examples, _prefs.Layout.CollapsedSampleFolders ?? [], OnSampleFolderExpandedChanged);

    private void OnSampleFolderExpandedChanged(SampleTreeNode folder)
    {
        var collapsed = (_prefs.Layout.CollapsedSampleFolders ?? []).Where(p => p != folder.Id).ToList();
        if (!folder.IsExpanded) collapsed.Add(folder.Id);
        _prefs.Layout.CollapsedSampleFolders = collapsed;
        _prefs.Save();
    }

    /// <summary>The bundled examples (empty if none could be read).</summary>
    public IReadOnlyList<ExampleScript> Examples { get; init; } = ExampleScripts.All;

    /// <summary>
    /// Opens an example as an untitled, unsaved copy — never run, nothing written to disk; Save
    /// asks where to save it, as for any new script. An example already open in an untitled tab is
    /// brought to the front instead of opened again.
    /// </summary>
    public void OpenExample(ExampleScript example)
    {
        if (Tabs.FirstOrDefault(t => t.FilePath is null && t.Example?.Id == example.Id) is { } open)
        {
            ActiveTab = open;
            return;
        }

        // Like opening a file: the initial blank tab goes if it is still pristine.
        EditorTabItemViewModel? blankToClose = null;
        if (Tabs.Count == 1 && Tabs[0].FilePath is null && Tabs[0].Example is null && !Tabs[0].IsDirty)
            blankToClose = Tabs[0];

        AddTab(new EditorTabItemViewModel(null, AvailableSdks, _prefs, this, example.Language, example.Kind)
        {
            Example = example,
        });

        if (blankToClose is not null)
            CloseTab(blankToClose);
    }

    // ── Sidebar layout (collapsible file tree) ────────────────────────────────

    // Was its own copy of the collapse/expand-plus-throttled-persist scheme,
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
    // disk, so there's no path to remember them by ("tab lifecycle (leftover)").
    // Called after every tab open/close/switch — a plain local JSON write on a low-frequency user
    // action, not worth the debounce Sidebar's own drag-frame writes need.
    private void PersistSession()
    {
        if (_restoringSession) return;
        _prefs.Session.OpenTabs = Tabs.Where(t => t.FilePath is not null).Select(t => t.FilePath!).ToList();
        _prefs.Session.ActiveTabPath = ActiveTab?.FilePath;
        SaveSessionSettings();
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
        _restoringSession = true;
        try
        {
            foreach (var path in savedOpenTabs)
            {
                if (!File.Exists(path)) continue;
                OpenTab(path);
                restoredAny = true;
            }
        }
        finally { _restoringSession = false; }

        if (!restoredAny)
        {
            OpenTab(null);
            return;
        }

        if (savedActivePath is { } activePath && _openTabs.TryGetValue(activePath, out var activeVm))
            ActiveTab = activeVm;
    }

    // Deliberately doesn't touch disk ("tab lifecycle") — a new script starts
    // empty and in-memory-only; it only becomes a real file the first time the user does Save or
    // Save As (SaveTabAsAsync below). language/kind are carried on the tab itself
    // (EditorTabItemViewModel.NewFileLanguage/NewFileKind) since there's no front matter on disk yet
    // for EditorsTabItemControl.SetDocumentContent/EditorTabsControl.CreateIntelliSenseAsync to read them
    // from.
    public void NewRpadFile(RpadLanguage language = RpadLanguage.CSharp, RpadKind kind = RpadKind.Statements)
    {
        var vm = new EditorTabItemViewModel(null, AvailableSdks, _prefs, this, language, kind);
        AddTab(vm);
    }

    // Ctrl+N / the new File > New File menu item — same as NewRpadFile above, but reads
    // the language+kind to use from EditorSettings instead of requiring an explicit choice, so it
    // never has to show NewFileKindDialog. The explicit File > New RPad File submenu (still asking via
    // the dialog) and the file tree's own "New File" (writes to disk immediately) are
    // both untouched — this only adds a third, dialog-free entry point.
    // File > New RPad File's choices: the registered languages.
    public IReadOnlyList<NewFileLanguageChoice> NewFileLanguages { get; }

    public void NewRpadFileWithDefaults() => NewRpadFile(_prefs.Editor.NewFileLanguage, _prefs.Editor.NewFileKind);

    public void OpenTab(string? filePath)
    {
        if (filePath is not null) filePath = Path.GetFullPath(filePath);
        if (filePath is not null && _openTabs.TryGetValue(filePath, out var existing))
        {
            ActiveTab = existing;
            RememberRecentFile(filePath!);
            return;
        }

        // Close the initial blank tab when the first real file is opened — only while it's still
        // pristine (CloseTab never saves, so a typed-into untitled tab must stay).
        EditorTabItemViewModel? blankToClose = null;
        if (filePath is not null && Tabs.Count == 1 && Tabs[0].FilePath is null && !Tabs[0].IsDirty)
            blankToClose = Tabs[0];

        var vm = new EditorTabItemViewModel(filePath, AvailableSdks, _prefs, this);
        if (filePath is not null)
        {
            _openTabs[filePath] = vm;
        }
        AddTab(vm);

        if (blankToClose is not null)
            CloseTab(blankToClose);
        if (filePath is not null) RememberRecentFile(filePath);
    }

    private void AddTab(EditorTabItemViewModel vm)
    {
        Tabs.Add(vm);
        ActiveTab = vm; // also persists the session (see the setter) — covers the Tabs.Add above too
    }

    // Called after a Save As (or the first Save of a brand-new, never-saved tab) actually writes
    // `newPath` to disk — rebinds the tab from "untitled" (or its old path) to the new one, the same
    // bookkeeping RenameFile already does for a file-tree rename, just triggered from the tab side
    // instead of the tree.
    public Task SaveTabAsAsync(EditorTabItemViewModel vm, Action<string> writeToPath)
    {
        if (vm.PendingSaveAs is { IsCompleted: false } pending) return pending;
        return vm.PendingSaveAs = SaveTabAsCoreAsync(vm, writeToPath);
    }

    private async Task SaveTabAsCoreAsync(EditorTabItemViewModel vm, Action<string> writeToPath)
    {
        var newPath = await _windowService.ShowSaveFileDialogAsync(vm.FileName, _fileSystemService.Folder);
        if (newPath is null) return; // cancelled

        // An untitled copy of a file (a recovered draft whose file was already open) never replaces
        // that file, nor a file another tab has open: either would overwrite the other version unasked.
        var fullPath = Path.GetFullPath(newPath);
        if ((vm.FilePath is null && vm.RestoredRecovery?.OriginalPath is { } original
                && string.Equals(Path.GetFullPath(original), fullPath, FilePathComparison))
            || (_openTabs.TryGetValue(fullPath, out var other) && other != vm))
        {
            await _windowService.ShowErrorAsync("Save Failed",
                $"'{Path.GetFileName(newPath)}' has its own version open or is the file this copy was made from. Save the copy to another file.");
            return;
        }

        // Choosing the tab's own file again is an in-place save: the picker's own "replace?" question
        // is about overwriting a file, not about one changed by another program meanwhile.
        if (vm.FilePath is { } ownPath && string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(ownPath), FilePathComparison))
        {
            var result = await InPlaceSave.CheckAndWriteAsync(vm, _windowService, () => SaveTabAsCoreAsync(vm, writeToPath));
            if (result.Status == InPlaceSaveStatus.Failed)
                await _windowService.ShowErrorAsync("Save Failed", result.Error);
            return;
        }

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
        vm.AcceptWrittenState(newPath);
        vm.IsDirty = false;
        _openTabs[newPath] = vm;
        RememberRecentFile(newPath);
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

    // ── IEditorTabActions — real bodies for EditorTabItemViewModel's tab-toolbar/context-menu
    // commands (these used to be empty-bodied and subscribed from outside, by
    // EditorTabsControl, a class that's neither the tab's View nor its ViewModel). SaveAsAsync/
    // Duplicate wrap the existing SaveTabAsAsync/DuplicateTab above (unchanged, still directly
    // callable/testable with an explicit writeToPath) — they just source that delegate from
    // vm.WriteToFile instead of taking it as a parameter, since a command has no other way to
    // supply one. WriteToFile is null only for the brief window between a tab being constructed and
    // its EditorsTabItemControl finishing construction (see EditorTabItemViewModel.WriteToFile's own
    // comment) — no command is reachable by the user before that, so these no-op rather than throw
    // if it somehow still happened.

    Task IEditorTabActions.SaveAsAsync(EditorTabItemViewModel vm) =>
        vm.WriteToFile is { } writeToFile ? SaveTabAsAsync(vm, writeToFile) : Task.CompletedTask;

    void IEditorTabActions.Duplicate(EditorTabItemViewModel vm)
    {
        if (vm.WriteToFile is { } writeToFile) DuplicateTab(vm, writeToFile);
    }

    void IEditorTabActions.OpenContainingFolder(EditorTabItemViewModel vm)
    {
        if (vm.FilePath is { } path) OpenContainingFolder(path);
    }

    // Moved here from EditorTabsControl.RequestCloseTabAsync — the confirmation
    // dialog/save-then-close flow needed EditorsTabItemControl.WriteFile, which now reaches this
    // class via vm.WriteToFile instead of being passed down from the View on every call. Explicit
    // (like the rest of IEditorTabActions) — external callers go through vm.CloseCommand, the same
    // path the tab's own [x] button/Ctrl+W use, rather than a second, parallel way to trigger this.
    async Task IEditorTabActions.CloseAsync(EditorTabItemViewModel vm)
    {
        await _recoveryGate.WaitAsync();
        try
        {
            if (!await ResolveUnsavedChangesBeforeCloseAsync(vm)) return;
            if (RestoresDrafts && !await CheckpointRecoveryCoreAsync(vm)) return;
            CloseTab(vm);
        }
        finally { _recoveryGate.Release(); }
    }

    // The single "may this dirty tab's content be dropped now?" decision, shared by a
    // single tab's close (CloseAsync above) and app quit (EditorTabsControl.
    // ConfirmCloseAllUnsavedTabsAsync). Runs BEFORE the tab is removed — removal itself no longer
    // saves anything, so a write failure can't be discovered only after the tab/editor is gone.
    //   - clean tab: nothing to lose.
    //   - named tab: written in place; on failure the error is shown and the user gets the same
    //     Save (As)/Don't Save/Cancel choice an untitled tab gets — otherwise a file that can never
    //     be written (read-only, gone, disk full) would make the tab impossible to close at all.
    //   - untitled tab: Save/Don't Save/Cancel, Save opening Save As.
    // Save As only clears IsDirty on success — still dirty means its dialog was cancelled or the
    // write failed, so that's "changed my mind about closing", not closing with the content lost.
    internal async Task<bool> ResolveUnsavedChangesBeforeCloseAsync(EditorTabItemViewModel vm)
    {
        if (!vm.IsDirty) return true;

        if (vm.FilePath is not null)
        {
            // A declined save (the file changed on disk and the user cancelled) stops the close too.
            var result = await SaveInPlaceAsync(vm);
            if (result.Status == InPlaceSaveStatus.Saved) return true;
            if (result.Status == InPlaceSaveStatus.Cancelled) return false;
            await _windowService.ShowErrorAsync("Save Failed", result.Error);
        }

        var choice = await ConfirmDiscardUnsavedChangesAsync(vm.FileName);
        if (choice == UnsavedChangesResult.Cancel) return false;
        if (choice == UnsavedChangesResult.DontSave) return true;

        await ((IEditorTabActions)this).SaveAsAsync(vm);
        return !vm.IsDirty;
    }

    // Writes a dirty named tab to its own path (the same serializer explicit Save uses, via
    // vm.WriteToFile), asking first if the file changed on disk (InPlaceSave). A clean or untitled tab
    // is a successful no-op. Fails closed: a dirty named tab whose editor isn't wired up (no
    // WriteToFile) can't be written, so it must not be reported as saved and then discarded.
    private Task<InPlaceSaveResult> SaveInPlaceAsync(EditorTabItemViewModel vm)
    {
        if (!vm.IsDirty || vm.FilePath is null) return Task.FromResult(InPlaceSaveResult.Saved);
        return InPlaceSave.SaveAsync(vm, _windowService, () => ((IEditorTabActions)this).SaveAsAsync(vm));
    }

    // Rename/move/delete flush every affected dirty tab first and abort the whole
    // operation (nothing closed, nothing touched on disk) if any of them can't be written or the
    // user declines to replace a file changed on disk.
    private async Task<bool> TrySaveTabsBeforeFileOperationAsync(IEnumerable<EditorTabItemViewModel> tabs, string errorTitle)
    {
        foreach (var tab in tabs)
        {
            var result = await SaveInPlaceAsync(tab);
            if (result.Status == InPlaceSaveStatus.Saved) continue;
            if (result.Status == InPlaceSaveStatus.Failed)
                _ = _windowService.ShowErrorAsync(errorTitle, result.Error);
            return false;
        }

        return true;
    }

    // "File → Import LINQPad Query..." — converts a .linq file (picked via a native
    // Open dialog, so it can live anywhere on disk, not just inside the current workspace folder) to
    // a sibling .rpad next to it, then opens that as a real tab. Not written into the CURRENT
    // workspace folder even if the .linq happens to live elsewhere — mirrors DuplicateTab's own
    // "next to the original" convention rather than always landing inside whatever workspace happens
    // to be open; if that means the new file isn't under the tracked folder, RefreshNodes below just
    // won't show it in the sidebar (the tab itself still opens and works fine either way).
    public Task ImportLinqFileAsync() => TrackWorkspaceOperation(ImportLinqFileCoreAsync);

    private async Task ImportLinqFileCoreAsync()
    {
        if (_restoreTask is { IsCompleted: false } restoring) await restoring;
        if (_disposed) return;
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

    // Asks whether to save a dirty tab's content before its close discards it — see
    // ResolveUnsavedChangesBeforeCloseAsync above, the sole caller. A thin passthrough to
    // _windowService.
    public Task<UnsavedChangesResult> ConfirmDiscardUnsavedChangesAsync(string fileName)
        => _windowService.ShowUnsavedChangesDialogAsync(fileName);

    // internal (not private): DeleteFile/MoveOrRenameOnDisk below also remove a tab directly,
    // bypassing the unsaved-changes confirmation CloseAsync above does — they flush affected tabs
    // themselves first (TrySaveTabsBeforeFileOperation). Removing a tab never saves it:
    // every caller must have resolved its unsaved content beforehand.
    internal void CloseTab(EditorTabItemViewModel vm)
    {
        if (vm.FilePath is not null) _openTabs.Remove(vm.FilePath);

        bool wasActive = ActiveTab == vm;
        Tabs.Remove(vm);
        if (wasActive)
            ActiveTab = Tabs.LastOrDefault(); // also persists the session (see the setter)
        else
            PersistSession(); // ActiveTab unchanged, so its setter won't fire — persist explicitly
    }

    // A new tab for the same file in vm's place — for a script whose language changed on disk,
    // which vm's editor services can't show. vm must have nothing unsaved.
    internal void ReopenTab(EditorTabItemViewModel vm)
    {
        var index = Tabs.IndexOf(vm);
        if (vm.FilePath is not { } path || index < 0) return;
        var wasActive = ActiveTab == vm;
        _openTabs.Remove(path);
        Tabs.RemoveAt(index);
        var reopened = new EditorTabItemViewModel(path, AvailableSdks, _prefs, this);
        _openTabs[path] = reopened;
        Tabs.Insert(index, reopened);
        if (wasActive)
            ActiveTab = reopened; // also persists the session (see the setter)
        else
            PersistSession();
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

    // Completes at once unless a tab has to be saved first and the user is asked about it.
    public Task DeleteFile(string filePath) => TrackWorkspaceOperation(() => DeleteFileCoreAsync(filePath));

    private async Task DeleteFileCoreAsync(string filePath)
    {
        // Directory.Exists/File.Delete are NOT interchangeable — File.Delete on a directory path
        // throws (silently swallowed below, before this fix), so a folder never actually left disk
        // even though the tree acted like it had.
        var isDirectory = Directory.Exists(filePath);

        // Affected tabs — the file itself, or (for a folder) every tab nested inside it.
        // They're flushed first (a failed write aborts the delete) and only closed once the delete
        // actually succeeded, so a failed delete leaves the very same tabs (editor state included)
        // open instead of closing and reloading them from disk.
        var affectedTabs = FindAffectedTabs(filePath, isDirectory);
        if (!await TrySaveTabsBeforeFileOperationAsync(affectedTabs, "Delete Failed")) return;

        try
        {
            if (isDirectory)
                Directory.Delete(filePath, recursive: true);
            else
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            // Report instead of the previous silent catch-and-ignore — a failed delete used to leave
            // the tree acting like the folder/file was gone while it was still on disk, with no way
            // to tell why.
            _ = _windowService.ShowErrorAsync(
                "Delete Failed", $"Could not delete '{Path.GetFileName(filePath)}':\n{ex.Message}");
            return;
        }

        foreach (var tab in affectedTabs)
            CloseTab(tab);
        FileTree.RemoveByPath(filePath);
    }

    public Task RenameFile(string filePath, string newName)
    {
        // Directory.Exists/File.Move are NOT interchangeable — File.Move on a directory path throws,
        // so renaming a folder always failed before this fix.
        // A folder's own name has no extension to preserve (unlike a .rpad file's).
        var isDirectory = Directory.Exists(filePath);
        var ext = isDirectory ? "" : Path.GetExtension(filePath);
        var dir = Path.GetDirectoryName(filePath)!;
        var newFileName = newName + ext;
        var newPath = Path.Combine(dir, newFileName);

        return MoveOrRenameOnDisk(filePath, newPath, "Rename Failed", "rename", $"to '{newFileName}'");
    }

    // File tree drag-and-drop — moves a node into a different folder (or the tree's
    // root, contextPath null), reusing the exact same target-directory resolution CreateFolderAsync/
    // CreateFileAsync already use (ResolveParentDir) — dropping onto a folder node moves INSIDE it,
    // onto a file node moves alongside it (same parent), onto empty tree space moves to the root. A
    // no-op (not an error) if that resolves to the node's own current parent — that's what dropping a
    // node back onto where it already lives looks like; dropping a folder onto its own descendant is
    // still a real error (Directory.Move already throws a sensible message for that on its own, no
    // separate check needed).
    public Task MoveFileOrFolder(string sourcePath, string? contextPath)
    {
        var targetDir = ResolveParentDir(contextPath);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var newPath = Path.Combine(targetDir, name);

        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(sourcePath), StringComparison.Ordinal))
            return Task.CompletedTask;

        return MoveOrRenameOnDisk(sourcePath, newPath, "Move Failed", "move", $"into '{Path.GetFileName(targetDir)}'");
    }

    // Shared by RenameFile (same directory, new name) and MoveFileOrFolder (new directory, same
    // name). Affected tab(s) are flushed to the OLD path first (a failed write aborts
    // the operation), and only closed/reopened at the new path once the move succeeded — a failed
    // move leaves the very same tabs open rather than closing and reloading them from disk.
    private Task MoveOrRenameOnDisk(
        string sourcePath, string newPath, string errorTitle, string errorVerb, string errorDestinationPhrase)
        => TrackWorkspaceOperation(() => MoveOrRenameCoreAsync(sourcePath, newPath, errorTitle, errorVerb, errorDestinationPhrase));

    private async Task MoveOrRenameCoreAsync(
        string sourcePath, string newPath, string errorTitle, string errorVerb, string errorDestinationPhrase)
    {
        var isDirectory = Directory.Exists(sourcePath);

        var affectedTabs = FindAffectedTabs(sourcePath, isDirectory);
        if (!await TrySaveTabsBeforeFileOperationAsync(affectedTabs, errorTitle)) return;

        try
        {
            if (isDirectory)
                Directory.Move(sourcePath, newPath);
            else
                File.Move(sourcePath, newPath);
        }
        catch (Exception ex)
        {
            _ = _windowService.ShowErrorAsync(
                errorTitle,
                $"Could not {errorVerb} '{Path.GetFileName(sourcePath)}' {errorDestinationPhrase}:\n{ex.Message}");
            return;
        }

        RefreshNodes();

        var oldPaths = affectedTabs.Select(t => t.FilePath!).ToList();
        foreach (var tab in affectedTabs)
            CloseTab(tab);
        foreach (var oldPath in oldPaths)
            OpenTab(isDirectory ? Path.Combine(newPath, Path.GetRelativePath(sourcePath, oldPath)) : newPath);
    }

    // The tab open on `path` itself plus, for a folder, every tab nested inside it.
    private List<EditorTabItemViewModel> FindAffectedTabs(string path, bool isDirectory)
    {
        var tabs = new List<EditorTabItemViewModel>();
        if (_openTabs.TryGetValue(path, out var tab)) tabs.Add(tab);
        if (isDirectory) tabs.AddRange(FindOpenTabsUnder(path).Select(kv => kv.Value));
        return tabs;
    }

    // Creates a new folder next to whatever's selected in the file tree — INSIDE the
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

    // File tree's "New File" — same target-directory resolution as CreateFolderAsync
    // (reused via ResolveParentDir below), but unlike "New Folder" the new file is also immediately
    // useful to open, so it's opened as a tab right away (same OpenTab used for double-click) rather
    // than left for the user to click on themselves.
    public async Task<string> CreateFileAsync(string? contextPath, RpadLanguage language, RpadKind kind)
    {
        var parentDir = ResolveParentDir(contextPath);
        var code = CodeTemplate.TakeCaret(_languageRegistry.NewScriptCode(language, kind)).Text;
        var path = _fileSystemService.CreateNewFile(parentDir, language, kind, code);
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
        // provider regardless of the parameter having a default.
        ILogger<MainWindowViewModel>? logger = null, IRecoveryStore? recovery = null,
        ConnectionNavigationViewModel? connectionNavigation = null)
    {
        _fileSystemService = fileSystemService;
        _windowService = windowService;
        _prefs = prefs;
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        _recovery = recovery;
        _recoveryMode = prefs.Appearance?.RestoreUnsavedTabs ?? true;
        // FileTreeViewModel now needs IPreferencesService (RenameGesture/
        // DeleteGesture) and ILanguageHostRegistry (per-language file icon), so this moves from a
        // property initializer (which would run before _prefs is assigned above) into the
        // constructor body.
        _languageRegistry = languageRegistry;
        NewFileLanguages = NewFileLanguageChoice.From(languageRegistry);
        var recent = new List<string>();
        foreach (var path in _prefs.Session.RecentFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { recent.Add(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        _prefs.Session.RecentFiles = recent.Distinct(FilePathComparer).Take(50).ToList();
        FileTree = new FileTreeViewModel(_prefs, languageRegistry);
        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        OpenRecentFileCommand = ReactiveCommand.CreateFromTask<string>(OpenScriptAsync);
        ClearRecentFilesCommand = ReactiveCommand.Create(() =>
        {
            _prefs.Session.RecentFiles = [];
            RecentFilesUpdated();
        }, this.WhenAnyValue(x => x.HasRecentFiles));
        OpenFileCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "Opening script failed"));
        OpenRecentFileCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "Opening recent script failed"));
        OpenDependenciesCommand = ReactiveCommand.CreateFromTask(OpenDependenciesWindowAsync);
        OpenPreferencesCommand = ReactiveCommand.CreateFromTask(_windowService.ShowPreferencesAsync);
        OpenAboutCommand = ReactiveCommand.CreateFromTask(_windowService.ShowAboutAsync);
        OpenSecretStoreCommand = ReactiveCommand.CreateFromTask(_windowService.ShowSecretStoreAsync);
        OpenConnectionsCommand = ReactiveCommand.CreateFromTask(() => OpenConnectionsAsync());
        ImportLinqFileCommand = ReactiveCommand.CreateFromTask(ImportLinqFileAsync);
        OpenAssemblyExplorerCommand = ReactiveCommand.CreateFromTask(
            () => _windowService.ShowAssemblyExplorerAsync(_fileSystemService.Folder));
        OpenTextFileCommand = ReactiveCommand.CreateFromTask(OpenTextFileAsync);
        OpenTextFileCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenTextFileCommand failed"));
        // Was silently swallowed — a failure opening one of these dialogs used to
        // vanish with no trace at all.
        OpenDependenciesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenDependenciesCommand failed"));
        OpenPreferencesCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenPreferencesCommand failed"));
        OpenAboutCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenAboutCommand failed"));
        OpenSecretStoreCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenSecretStoreCommand failed"));
        OpenConnectionsCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "OpenConnectionsCommand failed"));
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

        ConnectionNavigation = connectionNavigation;
        if (ConnectionNavigation is { } navigation)
        {
            navigation.RestoreCollapsedGroups(_prefs.Layout.CollapsedConnectionGroups);
            navigation.CollapsedGroupsChanged += groups =>
            {
                _prefs.Layout.CollapsedConnectionGroups = [.. groups];
                _prefs.Save();
            };
            // The attached badges follow the active script's own connections list.
            this.WhenAnyValue(x => x.ActiveTab)
                .Select(tab => tab?.WhenAnyValue(t => t.FrontMatter) ?? Observable.Return<RpadFrontMatter?>(null))
                .Switch()
                .Subscribe(frontMatter => navigation.SetAttached(frontMatter is null ? null : frontMatter.Connections ?? []));
            // A finished run may have changed connection schema cache entries (EnsureAsync reads
            // contracts before compiling): recheck statuses so the tree reflects successes and failures.
            this.WhenAnyValue(x => x.ActiveTab)
                .Select(tab => tab?.WhenAnyValue(t => t.RunState) ?? Observable.Return(RunState.Idle))
                .Switch()
                .Where(state => state == RunState.Idle)
                .Subscribe(state => { _ = navigation.RefreshStatusesAsync(); });
            RefreshConnectionNavigation();
        }

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
                _dispatcher.Post(() =>
                {
                    RefreshNodes();
                    if (!_disposed) ScriptsFolderChanged?.Invoke();
                });
        });
    }

    // Fire-and-forget wrapper for every call site that doesn't need to know when
    // the refresh actually lands (the constructor, the watcher above, every disk-mutating command
    // except CreateFolderAsync) — GetFilesTreeAsync's own Task.Run means the disk walk never blocks
    // whichever thread called this, only FileTree.Refresh's (already cheap, diff-based) merge runs
    // back on the UI thread once it resolves.
    private void RefreshNodes() => _ = RefreshNodesAsync();

    // CreateFolderAsync is the one caller that needs to actually await this — FileTree_CreateFolder
    // (MainWindow.axaml.cs) calls FileTreeView.StartRenameForPath immediately after, which needs the
    // new folder to already be in FileTree.Nodes to find it.
    //
    // Never throws — RefreshNodes discards the Task, and this used to be awaited from an
    // async void, so any walk failure (unreadable/vanished root, network path gone) escaped onto the
    // UI dispatcher as an unhandled exception. A failed walk keeps the previous tree as-is.
    // internal (not private) so tests can await a specific refresh.
    internal async Task RefreshNodesAsync()
    {
        if (_disposed) return;
        var version = ++_refreshVersion;
        IReadOnlyList<FileSystemItem> items;
        try
        {
            items = await _fileSystemService.GetFilesTreeAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File tree refresh failed");
            if (_disposed || version != _refreshVersion || _treeErrorReported) return;
            _treeErrorReported = true;
            _ = _windowService.ShowErrorAsync(
                "Folder Unavailable", $"Could not read '{_fileSystemService.Folder}':\n{ex.Message}");
            return;
        }

        if (_disposed || version != _refreshVersion) return;
        _treeErrorReported = false;
        FileTree.Refresh(items);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _fileSystemService.WatcherEvent -= OnWatcherEvent;
        _fileSystemService.StopWatcher();

        GC.SuppressFinalize(this);
    }
}
