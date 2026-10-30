using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.Services.Scaling;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Invoklet.UserControls;

public partial class EditorTabsControl : UserControl, IDisposable, ITabDiskHost, IActiveTabSource, ITabReverter
{
    private readonly Dictionary<EditorTabItemViewModel, (TabItem Tab, EditorsTabItemControl Control)> _tabs = new();
    private readonly TabLifecycleManager? _lifecycle;
    private readonly TabDiskSync? _diskSync;
    private readonly CancellationTokenSource _closed = new();
    // Tabs whose editor services are still being created: closing one cancels its creation.
    private readonly Dictionary<EditorTabItemViewModel, CancellationTokenSource> _creating = new();
    private IDisposable? _activeTabSub;
    private bool _disposed;
    private readonly ILogger<EditorsTabItemControl>? _runLogger;
    private readonly ConnectionDriverRegistry? _connectionDrivers;
    private readonly ConnectionSchemas? _connectionSchemas;
    private readonly EditorTimings? _timings;

    private MainWindowViewModel? _vm;

    // ── Set in DI constructor ─────────────────────────────────────────────────

    private readonly IThemeService? _themeService;
    private readonly IWindowService? _windowService;
    private readonly ILanguageHostRegistry? _registry;
    private readonly IPreferencesService? _prefs;
    private readonly IBuildCacheService? _buildCache;

    // ── Empty ctor: used by XAML previewer only ───────────────────────────────

    public EditorTabsControl()
    {
        InitializeComponent();
    }

    // ── DI ctor: used at runtime ──────────────────────────────────────────────

    public EditorTabsControl(
        IThemeService themeService,
        IWindowService windowService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IBuildCacheService buildCache, ILogger<EditorsTabItemControl>? runLogger = null,
        ConnectionDriverRegistry? connectionDrivers = null, ConnectionSchemas? connectionSchemas = null,
        TabLifecycleManager? lifecycle = null, EditorTimings? timings = null)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
        _timings = timings;
        _diskSync = new TabDiskSync(windowService);
        _connectionDrivers = connectionDrivers;
        _connectionSchemas = connectionSchemas;
        _themeService = themeService;
        _windowService = windowService;
        _registry = registry;
        _prefs = prefs;
        _buildCache = buildCache;
        _runLogger = runLogger;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    // ── Called by MainWindow after construction ───────────────────────────────

    public void Attach(MainWindowViewModel vm)
    {
        if (_vm is not null)
            throw new InvalidOperationException("EditorTabsControl can only be attached once.");

        _vm = vm;

        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;
        fileTabControl.SelectionChanged += OnFileTabSelectionChanged;

        _lifecycle?.Attach(() => _vm?.ActiveTab, OnContentNotRestored);
        vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _activeTabSub = vm.WhenAnyValue(x => x.ActiveTab).Subscribe(OnActiveTabChanged);
        vm.DependenciesWindowClosed += OnDependenciesWindowClosed;
        vm.ScriptsFolderChanged += OnScriptsFolderChanged;

        // Restores the previous session's open tabs
        // (in order) + active tab instead of always starting from one blank tab.
        _ = RestoreSessionAsync(vm);
    }

    // Done once the restored session's tabs all have their headers and the active one shows its
    // script: tabs appear one by one (each first creates its language services, see
    // OnTabsCollectionChanged), so the window stays hidden behind the splash until then.
    internal Task WorkspaceShown => _workspaceShown.Task;
    private readonly TaskCompletionSource _workspaceShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _sessionRestored;

    private async Task RestoreSessionAsync(MainWindowViewModel vm)
    {
        try { await vm.RestoreSessionAsync(); }
        finally
        {
            _sessionRestored = true;
            SignalWorkspaceShownIfDone();
        }
    }

    private void SignalWorkspaceShownIfDone()
    {
        if (_disposed || _sessionRestored && _vm is not null && _vm.Tabs.All(_tabs.ContainsKey))
            _workspaceShown.TrySetResult();
    }

    // Dependencies window copies its draft into the active tab's MdRunFrontMatter only on OK.
    // On close: if that changed anything, push the new packages/imports into that tab's own
    // IntelliSense instance and flush to disk immediately so the change survives an app
    // close/crash instead of waiting for the next Run or tab close.
    private void OnScriptsFolderChanged() => _ = CheckFilesChangedOnDiskAsync();

    private void OnDependenciesWindowClosed()
    {
        GetActiveControl()?.OnDependenciesEdited();
    }

    // ── Tab lifecycle ──────────────────────────────────────────────────────────

    private async void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;

        if (e.NewItems is not null)
        {
            foreach (EditorTabItemViewModel vm in e.NewItems)
            {
                // The expensive part (the language's editor services — see CreateIntelliSenseAsync)
                // runs off the UI thread so opening a script doesn't freeze the app. By the time we
                // get back here, the tab may already have been closed again (or the whole control
                // torn down) — don't resurrect it in that case.
                using var creation = new CancellationTokenSource();
                _creating[vm] = creation;
                IIntelliSenseService? intelliSense;
                try
                {
                    intelliSense = await CreateIntelliSenseAsync(vm, creation.Token);
                }
                finally
                {
                    _creating.Remove(vm);
                }
                if (_disposed || !(_vm?.Tabs.Contains(vm) ?? false))
                {
                    // The tab never got the service, so it is still ours to dispose. Nobody else owns
                    // vm's disposal in this case either: MainWindowViewModel.CloseTab doesn't dispose
                    // it itself, it relies on the (normally synchronous) OldItems branch below —
                    // which already ran and only cancelled the creation, since _tabs had no entry yet.
                    intelliSense?.Dispose();
                    vm.Dispose();
                    SignalWorkspaceShownIfDone();
                    continue;
                }

                // From here on the tab owns the service and disposes it with itself.
                var ctrl = CreateControl(vm, intelliSense);

                // Where vm is among the tabs already shown: the end, unless it took the place of one.
                var position = _vm!.Tabs.TakeWhile(t => !ReferenceEquals(t, vm)).Count(_tabs.ContainsKey);
                var tab = AddTabItem(ctrl, vm, fileTabControl, position);
                _tabs[vm] = (tab, ctrl);
                if (_lifecycle is not null)
                {
                    ctrl.SpillStore = _lifecycle.Spill;
                    _lifecycle.Register(vm, ctrl);
                }

                // ActiveTab may already have been set to vm (e.g. it's the tab that was just
                // opened) before this entry existed — OnActiveTabChanged no-ops in that case, so
                // select it explicitly now if it's still the active one.
                if (ReferenceEquals(_vm?.ActiveTab, vm))
                    fileTabControl.SelectedItem = tab;
                _lifecycle?.Rebalance();
                SignalWorkspaceShownIfDone();
            }
        }

        if (e.OldItems is not null)
        {
            foreach (EditorTabItemViewModel vm in e.OldItems)
            {
                if (_creating.TryGetValue(vm, out var creation)) creation.Cancel();
                if (!_tabs.TryGetValue(vm, out var entry)) continue;
                // No save here: a write failure discovered only now would have nowhere
                // to go — the tab and its editor are being destroyed. Every removal path resolves
                // unsaved content BEFORE removing (MainWindowViewModel.
                // ResolveUnsavedChangesBeforeCloseAsync / TrySaveTabsBeforeFileOperation).
                _lifecycle?.Unregister(vm);
                entry.Control.Dispose();
                fileTabControl.Items.Remove(entry.Tab);
                _tabs.Remove(vm);
                vm.Dispose();
            }
            SignalWorkspaceShownIfDone();
        }
    }

    // ── Selection sync ─────────────────────────────────────────────────────────

    private void OnFileTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc || _vm is null) return;
        var selected = _tabs.FirstOrDefault(kv => kv.Value.Tab == tc.SelectedItem as TabItem).Key;

        // tc.SelectedItem transiently goes null while Items is being mutated — e.g. OpenTab closing
        // the initial blank tab right after opening a real file removes the blank tab's TabItem
        // before the new tab's own TabItem exists yet (CreateIntelliSenseAsync is still loading it off
        // the UI thread), leaving the TabControl briefly empty. That's not a real "no tab selected"
        // user action; nulling ActiveTab here would strand it permanently, since the code above that
        // would normally re-select the new tab once it finishes loading only does so
        // `if (ReferenceEquals(_vm?.ActiveTab, vm))`.
        if (selected is null && _vm.Tabs.Count > 0) return;

        _vm.ActiveTab = selected;
    }

    private void OnActiveTabChanged(EditorTabItemViewModel? vm)
    {
        if (vm is null) return;

        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;
        if (_tabs.TryGetValue(vm, out var entry) && fileTabControl.SelectedItem != entry.Tab)
            fileTabControl.SelectedItem = entry.Tab;

        _lifecycle?.Activated(vm);
    }

    // Only the tab shown colours its text.
    private void SyncHighlighting()
    {
        foreach (var (tabVm, (_, ctrl)) in _tabs)
            ctrl?.SetHighlightingActive(ReferenceEquals(tabVm, _vm?.ActiveTab));
    }

    internal Task IdleForTests() => _lifecycle?.WhenIdleAsync() ?? Task.CompletedTask;

    internal bool IsLoadedForTests(EditorTabItemViewModel vm) => _lifecycle?.IsContentLoaded(vm) ?? HasControl(vm);

    // A tab's text could not be brought back from disk: it stays empty and read-only, and saving it fails,
    // so nothing is overwritten.
    private void OnContentNotRestored(EditorTabItemViewModel tab)
        => _ = _windowService?.ShowErrorAsync("Tab Not Loaded", $"Could not read back the text of '{tab.FileName}'.");

    // ── Theme ──────────────────────────────────────────────────────────────────

    // Test-only (InternalsVisibleTo): the control showing a tab.
    internal EditorsTabItemControl? ControlForTests(EditorTabItemViewModel vm) => _tabs.TryGetValue(vm, out var entry) ? entry.Control : null;

    private void OnThemeChanged(ActiveTheme theme)
    {
        foreach (var (_, ctrl) in _tabs.Values)
            ctrl.ApplyTheme(theme);
    }

    // ── Files changed by other programs ───────────────────────────────────────

    /// <summary>
    /// Brings open tabs up to date with files another program changed (see <see cref="TabDiskSync"/>).
    /// Runs when Invoklet's window is activated and when the scripts folder reports a change.
    /// </summary>
    public Task CheckFilesChangedOnDiskAsync() => _diskSync?.CheckAsync(this, _closed.Token) ?? Task.CompletedTask;

    // The per-tab confirmation above only fires when a
    // SPECIFIC tab is closed — quitting the whole app skipped it entirely. MainWindow's Closing
    // handler checks this first (cheap, synchronous) before deciding whether it even needs to cancel
    // the close and await ConfirmCloseAllUnsavedTabsAsync below. Dirty named tabs count
    // too — they used to be written by Dispose after the window was already gone, so a failed
    // write lost the edits silently.
    public bool HasUnsavedTabs()
        => _vm?.Tabs.Any(t => t.IsDirty) ?? false;

    // Walks every dirty tab (not just the active one) through the same decision a single tab's close
    // uses (MainWindowViewModel.ResolveUnsavedChangesBeforeCloseAsync: named tabs are written in
    // place; untitled tabs and failed writes ask Save/Don't Save/Cancel) — stops at the first one
    // that can't be resolved rather than working through the rest, same as VS Code's behavior on
    // quit. Deliberately doesn't remove tabs from Tabs or touch _tabs itself — if the quit is
    // cancelled, every tab must still be there exactly as it was.
    public async Task<bool> ConfirmCloseAllUnsavedTabsAsync()
    {
        if (_vm is null) return true;

        // Snapshotted: nothing in this loop removes a tab, but SaveTabAsAsync does rebind FilePath —
        // iterating a live view of Tabs while that happens is asking for trouble even if today's
        // mutation pattern happens not to invalidate the enumerator.
        foreach (var vm in _vm.Tabs.ToList())
        {
            if (!vm.IsDirty || !_tabs.ContainsKey(vm)) continue;
            if (!await _vm.ResolveUnsavedChangesBeforeCloseAsync(vm)) return false;
        }

        return true;
    }

    // A tab whose text is on disk is read from its file when it is selected again, so there is nothing to reload.
    private bool HasControl(EditorTabItemViewModel vm) => _tabs.TryGetValue(vm, out var entry) && !entry.Control.IsContentReleased;

    private EditorsTabItemControl? GetActiveControl()
    {
        if (_vm?.ActiveTab is { } vm && _tabs.TryGetValue(vm, out var entry))
            return entry.Control;
        return null;
    }

    // ── "Show all open tabs" dropdown ───────────────────────────────────────────

    private void ShowAllTabs_Click(object? sender, RoutedEventArgs e)
    {
        if (BuildAllTabsMenu() is not { } menu || sender is not Control anchor) return;
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    // internal (not private): lets tests inspect the built menu's items/headers/click-wiring
    // directly, rather than only through the ContextMenu popup interaction itself, which headless
    // Avalonia has no reliable way to drive. Built fresh on every click
    // (not a persistent binding — this control doesn't set its own DataContext to the ViewModel, it
    // talks to it through _vm directly everywhere else too) rather than keeping one around that
    // would go stale the moment a tab opens/closes.
    internal ContextMenu? BuildAllTabsMenu()
    {
        if (_vm is null) return null;

        var menu = new ContextMenu();
        foreach (var tabVm in _vm.Tabs)
        {
            // Fully qualified with global:: on top — Control (an EditorTabsControl base class) has
            // its own inherited FontWeight instance property, shadowing the enum type name throughout
            // this whole class ("cannot be accessed with an instance reference" without the
            // qualification); and Invoklet.Avalonia is itself a real sibling namespace here
            // (Invoklet.Avalonia.Themes etc.), so plain "Avalonia.Media" would resolve relative to
            // Invoklet first and fail to find it, rather than falling back to the global Avalonia.
            var weight = ReferenceEquals(tabVm, _vm.ActiveTab)
                ? global::Avalonia.Media.FontWeight.Bold
                : global::Avalonia.Media.FontWeight.Normal;
            var header = new TextBlock { Text = tabVm.FileName, FontWeight = weight };
            if (tabVm.IsDirty && _themeService?.GetBrush("Invoklet.Tab.DirtyBrush") is { } dirtyBrush)
                header.Foreground = dirtyBrush;

            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _vm.ActiveTab = tabVm;
            menu.Items.Add(item);
        }

        return menu;
    }

    // ── "+" button ─────────────────────────────────────────────────────────────

    private async void AddFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var kind = await new Windows.NewFileKindDialog().ShowDialog<MdRunKind?>(owner);
        if (kind is null) return; // cancelled

        _vm.NewMdRunFile(kind: kind.Value);
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspaceShown.TrySetResult();
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            _vm.DependenciesWindowClosed -= OnDependenciesWindowClosed;
            _vm.ScriptsFolderChanged -= OnScriptsFolderChanged;
        }

        _activeTabSub?.Dispose();
        _closed.Cancel();
        _lifecycle?.Detach();
        foreach (var creation in _creating.Values) creation.Cancel();

        // No save here: unsaved content was already resolved by MainWindow's Closing
        // confirmation — saving again would overwrite a "Don't Save" choice and report a failure
        // into a window that no longer exists.
        foreach (var (tabVm, (_, ctrl)) in _tabs)
        {
            ctrl.Dispose();
            tabVm.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Each tab gets its own editor services from its language's module: they are stateful (one
    // workspace and document per instance — each script can reference different NuGet packages), so
    // a shared instance would let one tab's references and last-synced text stomp another tab's.
    // Creating one can be real disk/CPU work with no Avalonia dependency (C# loads metadata for every
    // BCL assembly), so it runs off the UI thread instead of freezing the app on open.
    //
    // The language comes from the file's own front matter (the same cheap parse EditorsTabItemControl.
    // SetDocumentContent does again later) or, for a brand-new, never-saved tab (vm.FilePath is null,
    // nothing to peek), from the tab's own pending choice — a new VB tab must not get the services of
    // the default language. An invalid document (PeekDocumentLanguage → null) opens as raw text, and a
    // language without editor services gets none: both without any language-specific test here.
    // A creation cancelled because the tab closed comes back as null.
    private async Task<IIntelliSenseService?> CreateIntelliSenseAsync(EditorTabItemViewModel vm, CancellationToken cancellationToken)
    {
        var language = vm.RestoredRecovery is { } recovered
            ? recovered.Document?.FrontMatter.Language
            : vm.FilePath is null ? vm.NewFileLanguage : MdRunFilePeek.PeekDocumentLanguage(vm.FilePath);
        if (_registry is not { } registry || language is not { } known || known == MdRunLanguage.Unknown) return null;
        var id = known.ToLanguageId();
        try
        {
            return await Task.Run(() => registry.CreateIntelliSense(id, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Avalonia controls (CodeTextEditor, InitializeComponent, ...) must be constructed on the
    // UI thread, so the control itself is created here, synchronously.
    private EditorsTabItemControl CreateControl(EditorTabItemViewModel vm, IIntelliSenseService? intelliSense)
    {
        return new EditorsTabItemControl(vm,
            _themeService!,
            _windowService!,
            _registry!,
            _prefs!,
            intelliSense,
            _buildCache!, _runLogger, _connectionDrivers, _connectionSchemas, _timings
        );
    }

    private static TabItem AddTabItem(EditorsTabItemControl ctrl, EditorTabItemViewModel vm, TabControl tabControl, int position)
    {
        ctrl.Resources.TryGetResource("TabHeaderTemplate", null, out var tmpl);
        var tab = new TabItem
        {
            Header = vm,
            HeaderTemplate = (IDataTemplate)tmpl!,
            Content = ctrl,
        };
        tabControl.Items.Insert(position, tab);
        return tab;
    }
}
