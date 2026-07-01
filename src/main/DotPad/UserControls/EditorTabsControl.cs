using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using DotPad.Assets.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using DotPad.Windows.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace DotPad.UserControls;

public partial class EditorTabsControl : UserControl, IDisposable
{
    private readonly Dictionary<EditorTabItemViewModel, (TabItem Tab, EditorsTabItemControl Control)> _tabs = new();

    private MainWindowViewModel?   _vm;
    private IThemeService?         _themeService;
    private ILanguageHostRegistry? _registry;
    private IPreferencesService?   _prefs;
    private ILoggerFactory?        _loggerFactory;
    private IIntelliSenseService?  _intelliSense;
    private IDisposable?           _activeTabSub;

    public EditorTabsControl()
    {
        InitializeComponent();
    }

    public void Initialize(
        MainWindowViewModel   vm,
        IThemeService         themeService,
        ILanguageHostRegistry registry,
        IPreferencesService   prefs,
        ILoggerFactory        loggerFactory)
    {
        _vm            = vm;
        _themeService  = themeService;
        _registry      = registry;
        _prefs         = prefs;
        _loggerFactory = loggerFactory;

        _themeService.ThemeChanged += OnThemeChanged;

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
                var tab  = AddTabItem(ctrl, vm, fileTabControl);
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

    // ── "+" button ─────────────────────────────────────────────────────────────

    private void AddFile_Click(object? sender, RoutedEventArgs e)
        => _vm?.NewFile();

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        if (_vm is not null)           _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
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
        _intelliSense ??= new CSharpIntelliSenseService(
            new CSharpReferenceService().GetDefault(),
            _loggerFactory!.CreateLogger<CSharpIntelliSenseService>());

        return new EditorsTabItemControl(
            vm, _themeService!, _registry!, _prefs!, _intelliSense,
            _themeService!.GetBrush("DotPad.Editor.BackgroundBrush"));
    }

    private static TabItem AddTabItem(EditorsTabItemControl ctrl, EditorTabItemViewModel vm, TabControl tabControl)
    {
        ctrl.Resources.TryGetResource("TabHeaderTemplate", null, out var tmpl);
        var tab = new TabItem
        {
            Header         = vm,
            HeaderTemplate = (IDataTemplate)tmpl!,
            Content        = ctrl,
        };
        tabControl.Items.Add(tab);
        return tab;
    }
}
