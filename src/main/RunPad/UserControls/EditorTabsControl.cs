using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using ReactiveUI;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;

namespace RunPad.UserControls;

public partial class EditorTabsControl : UserControl, IDisposable
{
    private readonly Dictionary<EditorTabItemViewModel, (TabItem Tab, EditorsTabItemControl Control)> _tabs = new();
    private IDisposable? _activeTabSub;
    private bool _disposed;

    private MainWindowViewModel? _vm;

    // ── Set in DI constructor ─────────────────────────────────────────────────

    private readonly IThemeService? _themeService;
    private readonly IWindowService? _windowService;
    private readonly ILanguageHostRegistry? _registry;
    private readonly IPreferencesService? _prefs;
    private readonly Func<IIntelliSenseService>? _intelliSenseFactory;
    private readonly CSharpReferenceService? _referenceService;
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
        Func<IIntelliSenseService> intelliSenseFactory,
        CSharpReferenceService referenceService,
        IBuildCacheService buildCache)
    {
        InitializeComponent();
        _themeService = themeService;
        _windowService = windowService;
        _registry = registry;
        _prefs = prefs;
        _intelliSenseFactory = intelliSenseFactory;
        _referenceService = referenceService;
        _buildCache = buildCache;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    // ── Called by MainWindow after construction ───────────────────────────────

    public void Attach(MainWindowViewModel vm)
    {
        _vm = vm;

        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;
        fileTabControl.SelectionChanged += OnFileTabSelectionChanged;

        vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _activeTabSub = vm.WhenAnyValue(x => x.ActiveTab).Subscribe(OnActiveTabChanged);
        vm.DependenciesWindowClosed += OnDependenciesWindowClosed;

        // TODO.md, "tab lifecycle (leftover)": restores the previous session's open tabs
        // (in order) + active tab instead of always starting from one blank tab.
        vm.RestoreSession();
    }

    // Dependencies window mutates the active tab's RpadFrontMatter in place, in memory only.
    // On close: push the new package set into that tab's own IntelliSense instance so
    // completion/diagnostics see it without reopening the tab, and flush to disk immediately so the
    // change survives an app close/crash instead of waiting for the next Run or tab close.
    private void OnDependenciesWindowClosed()
    {
        var ctrl = GetActiveControl();
        ctrl?.RefreshIntelliSensePackages();
        ctrl?.SaveIfNeeded();
    }

    // ── Tab lifecycle ──────────────────────────────────────────────────────────

    private async void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;

        if (e.NewItems is not null)
        {
            foreach (EditorTabItemViewModel vm in e.NewItems)
            {
                // The expensive part (Roslyn workspace + BCL metadata references — see
                // CreateControlAsync) runs off the UI thread so opening a script doesn't freeze the
                // app. By the time we get back here, the tab may already have been closed again
                // (or the whole control torn down) — don't resurrect it in that case.
                var ctrl = await CreateControlAsync(vm);
                if (_disposed || !(_vm?.Tabs.Contains(vm) ?? false))
                {
                    // Nobody else owns vm's disposal in this case: MainWindowViewModel.CloseTab
                    // doesn't dispose it itself, it relies on the (normally synchronous) OldItems
                    // branch below — which already ran and no-opped, since _tabs had no entry yet.
                    ctrl.Dispose();
                    vm.Dispose();
                    continue;
                }

                var tab = AddTabItem(ctrl, vm, fileTabControl);
                _tabs[vm] = (tab, ctrl);

                // ActiveTab may already have been set to vm (e.g. it's the tab that was just
                // opened) before this entry existed — OnActiveTabChanged no-ops in that case, so
                // select it explicitly now if it's still the active one.
                if (ReferenceEquals(_vm?.ActiveTab, vm))
                    fileTabControl.SelectedItem = tab;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (EditorTabItemViewModel vm in e.OldItems)
            {
                if (!_tabs.TryGetValue(vm, out var entry)) continue;
                entry.Control.SaveIfNeeded();
                entry.Control.Dispose();
                fileTabControl.Items.Remove(entry.Tab);
                _tabs.Remove(vm);
                vm.Dispose();
            }
        }
    }

    // ── Selection sync ─────────────────────────────────────────────────────────

    private void OnFileTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc || _vm is null) return;
        var selected = _tabs.FirstOrDefault(kv => kv.Value.Tab == tc.SelectedItem as TabItem).Key;

        // tc.SelectedItem transiently goes null while Items is being mutated — e.g. OpenTab closing
        // the initial blank tab right after opening a real file removes the blank tab's TabItem
        // before the new tab's own TabItem exists yet (CreateControlAsync is still loading it off
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
    }

    // ── Theme ──────────────────────────────────────────────────────────────────

    private void OnThemeChanged(ActiveTheme theme)
    {
        foreach (var (_, ctrl) in _tabs.Values)
            ctrl.ApplyTheme(theme);
    }

    // ── Hotkey actions ─────────────────────────────────────────────────────────

    public void RunOrContinueActive()
    {
        if (GetActiveControl() is not { } ctrl || _vm?.ActiveTab is not { } vm) return;
        if (vm.EnableRunButton)
        {
            var platform = vm.SelectedSdk ?? vm.AvailableSdks.FirstOrDefault();
            if (platform is not null)
                _ = ctrl.RunAsync(vm.DebugModeEnabled, platform);
        }
        else if (vm.IsPaused)
            ctrl.ResumeDebug(DebugAction.Continue);
    }

    public void CancelActive()
        => GetActiveControl()?.Cancel();

    public void StepOverActive()
    {
        if (_vm?.ActiveTab?.IsPaused == true) GetActiveControl()?.ResumeDebug(DebugAction.StepOver);
    }

    public void StepInActive()
    {
        if (_vm?.ActiveTab?.IsPaused == true) GetActiveControl()?.ResumeDebug(DebugAction.StepIn);
    }

    public void StepOutActive()
    {
        if (_vm?.ActiveTab?.IsPaused == true) GetActiveControl()?.ResumeDebug(DebugAction.StepOut);
    }

    // Explicit Save (Ctrl+S) — the gap a user hit as "IsDirty doesn't reset when I save" (2026-07-17):
    // there was no actual Save action anywhere, only implicit saves (tab close/Run/etc.) or the
    // always-dialog "Save As", so Ctrl+S out of muscle memory matched nothing and silently did
    // nothing. A tab with a real path just writes in place (SaveIfNeeded — despite the name, it
    // writes unconditionally whenever FilePath is set, IsDirty or not, which is exactly explicit-Save
    // semantics); an untitled tab has nowhere to write to yet, so it falls through to the same
    // Save As flow as the tab context menu — matching every other editor's Ctrl+S-on-untitled
    // behavior.
    public void SaveActive()
    {
        if (GetActiveControl() is not { } ctrl || _vm?.ActiveTab is not { } vm) return;
        if (vm.FilePath is not null)
            ctrl.SaveIfNeeded();
        else
            vm.SaveAsCommand.Execute().Subscribe();
    }

    // Ctrl+Shift+S — always opens the Save As dialog, even for a tab that already has a path, matching
    // every other editor's "Save As" (as opposed to Ctrl+S/SaveActive above, which only falls through
    // to the dialog for an untitled tab). Same vm.SaveAsCommand the tab's own context menu item uses
    // (EditorTabItemControl.axaml) — this is just a second entry point to it.
    public void SaveAsActive()
    {
        if (_vm?.ActiveTab is not { } vm || GetActiveControl() is null) return;
        vm.SaveAsCommand.Execute().Subscribe();
    }

    public void FormatActive()
        => _ = GetActiveControl()?.FormatAsync();

    // Ctrl+Alt+U — ReSharper-style "add all missing usings" (live user request 2026-07-27).
    public void ResolveAllUsingsActive()
        => _ = GetActiveControl()?.ResolveAllUsingsAsync();

    public void GoToLineActive()
        => GetActiveControl()?.ShowGoToLine();

    // Ctrl+W's entry point — replaces the old direct vm.CloseActiveTab() call (MainWindow.axaml.cs)
    // now that closing needs to go through the same confirmation as the tab's own [x] button below.
    // Routed through the tab's own CloseCommand (real body: MainWindowViewModel.IEditorTabActions.
    // CloseAsync, ANALYSIS.md #102) rather than a second, parallel way to trigger the same close flow.
    public void CloseActiveTab()
    {
        if (_vm?.ActiveTab is not { } vm || GetActiveControl() is null) return;
        vm.CloseCommand.Execute().Subscribe();
    }

    // TODO.md, "tab lifecycle (leftover)": the per-tab confirmation above only fires when a
    // SPECIFIC tab is closed — quitting the whole app skipped it entirely, discarding every unsaved
    // untitled tab's content at once with no warning. MainWindow's Closing handler checks this first
    // (cheap, synchronous) before deciding whether it even needs to cancel the close and await
    // ConfirmCloseAllUnsavedTabsAsync below.
    public bool HasUnsavedUntitledTabs()
        => _vm?.Tabs.Any(t => t.FilePath is null && t.IsDirty) ?? false;

    // Walks every dirty untitled tab (not just the active one) asking the same Save/Don't
    // Save/Cancel question RequestCloseTabAsync asks for a single tab — stops at the first Cancel
    // (or the first Save whose own Save As dialog gets cancelled) rather than working through the
    // rest, same as VS Code's behavior on quit. Deliberately doesn't remove tabs from Tabs or touch
    // _tabs itself — the whole window is about to close either way, so there's nothing to tidy up in
    // the UI, only content to not lose.
    public async Task<bool> ConfirmCloseAllUnsavedTabsAsync()
    {
        if (_vm is null) return true;

        // Snapshotted: nothing in this loop removes a tab, but SaveTabAsAsync does rebind FilePath —
        // iterating a live view of Tabs while that happens is asking for trouble even if today's
        // mutation pattern happens not to invalidate the enumerator.
        foreach (var vm in _vm.Tabs.ToList())
        {
            if (vm.FilePath is not null || !vm.IsDirty) continue;
            if (!_tabs.TryGetValue(vm, out var entry)) continue;

            var choice = await _vm.ConfirmDiscardUnsavedChangesAsync(vm.FileName);
            if (choice == UnsavedChangesResult.Cancel) return false;

            if (choice == UnsavedChangesResult.Save)
            {
                await _vm.SaveTabAsAsync(vm, entry.Control.WriteFile);
                if (vm.FilePath is null) return false; // Save As was itself cancelled/failed
            }
        }

        return true;
    }

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
    // Avalonia has no reliable way to drive. TODO.md, "tab strip": built fresh on every click
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
            // qualification); and RunPad.Avalonia is itself a real sibling namespace here
            // (RunPad.Avalonia.Themes etc.), so plain "Avalonia.Media" would resolve relative to
            // RunPad first and fail to find it, rather than falling back to the global Avalonia.
            var weight = ReferenceEquals(tabVm, _vm.ActiveTab)
                ? global::Avalonia.Media.FontWeight.Bold
                : global::Avalonia.Media.FontWeight.Normal;
            var header = new TextBlock { Text = tabVm.FileName, FontWeight = weight };
            if (tabVm.IsDirty && _themeService?.GetBrush("RunPad.Tab.DirtyBrush") is { } dirtyBrush)
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

        var kind = await new Windows.NewFileKindDialog().ShowDialog<RpadKind?>(owner);
        if (kind is null) return; // cancelled

        _vm.NewRpadFile(kind: kind.Value);
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            _vm.DependenciesWindowClosed -= OnDependenciesWindowClosed;
        }

        _activeTabSub?.Dispose();

        foreach (var (tabVm, (_, ctrl)) in _tabs)
        {
            ctrl.SaveIfNeeded();
            ctrl.Dispose();
            tabVm.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<EditorsTabItemControl> CreateControlAsync(EditorTabItemViewModel vm)
    {
        // Each tab gets its own IntelliSense instance — see the comment on the
        // Func<IIntelliSenseService> registration in App.axaml.cs for why this can't be shared.
        // Constructing it builds a fresh Roslyn AdhocWorkspace and loads metadata references for
        // every BCL assembly (see CSharpReferenceService.GetDefault) — real disk/CPU work with no
        // Avalonia dependency, so it runs off the UI thread instead of freezing the app on open.
        //
        // IntelliSense is C#-only today (see CSharpIntelliSenseService) — peek the file's own front
        // matter here (the same cheap parse EditorsTabItemControl.SetDocumentContent does again
        // later) so a non-C# tab never gets a C# IntelliSense instance built for it in the first
        // place, rather than building one and leaving it unused. A brand-new, never-saved tab
        // (vm.FilePath is null) has no file to peek — PeekLanguage(null) always answers C#, which
        // used to be harmless only because "New File" wrote the real front matter to disk before
        // ever opening a tab (see NewRpadFile's old eager-write). Now that creation is deferred (see
        // NewRpadFile), a new VB tab would otherwise get a live C# IntelliSense instance wired up
        // against VB source text until its first save — read the tab's own pending choice instead.
        var language = vm.FilePath is null ? vm.NewFileLanguage : RpadFilePeek.PeekLanguage(vm.FilePath);
        var factory = _intelliSenseFactory;
        var intelliSense = factory is not null && language == RpadLanguage.CSharp
            ? await Task.Run(factory.Invoke)
            : null;

        // Avalonia controls (CodeTextEditor, InitializeComponent, ...) must be constructed on the
        // UI thread, so the control itself is still created here, synchronously.
        return new EditorsTabItemControl(vm,
            _themeService!,
            _windowService!,
            _registry!,
            _prefs!,
            intelliSense,
            _referenceService!,
            _buildCache!
        );
    }

    private static TabItem AddTabItem(EditorsTabItemControl ctrl, EditorTabItemViewModel vm, TabControl tabControl)
    {
        ctrl.Resources.TryGetResource("TabHeaderTemplate", null, out var tmpl);
        var tab = new TabItem
        {
            Header = vm,
            HeaderTemplate = (IDataTemplate)tmpl!,
            Content = ctrl,
        };
        tabControl.Items.Add(tab);
        return tab;
    }
}
