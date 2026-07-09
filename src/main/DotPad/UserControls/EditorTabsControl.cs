using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using DotPad.Windows.Models;
using ReactiveUI;

namespace DotPad.UserControls;

public partial class EditorTabsControl : UserControl, IDisposable
{
    private readonly Dictionary<EditorTabItemViewModel, (TabItem Tab, EditorsTabItemControl Control)> _tabs = new();
    private IDisposable? _activeTabSub;
    private bool _disposed;

    private MainWindowViewModel? _vm;

    // ── Set in DI constructor ─────────────────────────────────────────────────

    private readonly IThemeService? _themeService;
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
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        Func<IIntelliSenseService> intelliSenseFactory,
        CSharpReferenceService referenceService,
        IBuildCacheService buildCache)
    {
        InitializeComponent();
        _themeService = themeService;
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

        vm.OpenTab(filePath: null);
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

    public void FormatActive()
        => _ = GetActiveControl()?.FormatAsync();

    public void GoToLineActive()
        => GetActiveControl()?.ShowGoToLine();

    private EditorsTabItemControl? GetActiveControl()
    {
        if (_vm?.ActiveTab is { } vm && _tabs.TryGetValue(vm, out var entry))
            return entry.Control;
        return null;
    }

    // ── "+" button ─────────────────────────────────────────────────────────────

    private void AddFile_Click(object? sender, RoutedEventArgs e)
        => _vm?.NewRpadFile();

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
        var factory = _intelliSenseFactory;
        var intelliSense = factory is null ? null : await Task.Run(factory.Invoke);

        // Avalonia controls (CodeTextEditor, InitializeComponent, ...) must be constructed on the
        // UI thread, so the control itself is still created here, synchronously.
        return new EditorsTabItemControl(vm,
            _themeService!,
            _registry!,
            _prefs!,
            intelliSense,
            _referenceService!,
            _buildCache!,
            _themeService!.GetBrush("DotPad.Editor.BackgroundBrush"));
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
