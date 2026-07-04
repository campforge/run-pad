using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using DotPad.Assets.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using DotPad.Windows.Models;
using ReactiveUI;

namespace DotPad.UserControls;

public partial class EditorTabsControl : UserControl, IDisposable
{
    private readonly Dictionary<EditorTabItemViewModel, (TabItem Tab, EditorsTabItemControl Control)> _tabs = new();
    private IDisposable? _activeTabSub;

    private MainWindowViewModel? _vm;

    // ── Set in DI constructor ─────────────────────────────────────────────────

    private readonly IThemeService? _themeService;
    private readonly ILanguageHostRegistry? _registry;
    private readonly IPreferencesService? _prefs;
    private readonly IIntelliSenseService? _intelliSense;

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
        IIntelliSenseService intelliSense)
    {
        InitializeComponent();
        _themeService = themeService;
        _registry = registry;
        _prefs = prefs;
        _intelliSense = intelliSense;
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

        vm.OpenTab(filePath: null);
    }

    // ── Tab lifecycle ──────────────────────────────────────────────────────────

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;

        if (e.NewItems is not null)
        {
            foreach (EditorTabItemViewModel vm in e.NewItems)
            {
                var ctrl = CreateControl(vm);
                var tab = AddTabItem(ctrl, vm, fileTabControl);
                _tabs[vm] = (tab, ctrl);
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
                // BUG-2: vm.Dispose() не вызывается. EditorTabItemViewModel подписывается на
                // _prefs.Changed и никогда не отписывается → утечка ссылки через делегат.
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
            _ = ctrl.RunAsync(vm.DebugModeEnabled, vm.SelectedDotnetPlatform ?? vm.DotnetPlatforms.First());
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
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        if (_vm is not null) _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        _activeTabSub?.Dispose();

        foreach (var (_, ctrl) in _tabs.Values)
        {
            ctrl.SaveIfNeeded();
            ctrl.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private EditorsTabItemControl CreateControl(EditorTabItemViewModel vm)
    {
        return new EditorsTabItemControl(vm,
            _themeService!,
            _registry!,
            _prefs!,
            _intelliSense,
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
