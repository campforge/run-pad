using System.Collections.ObjectModel;
using DotPad.Assets.Models;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class PreferencesWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IThemeService       _themeService;
    private readonly IPreferencesService _prefs;

    private InterfaceTheme? _selectedInterfaceTheme;
    private EditorTheme?    _selectedEditorTheme;

    public ObservableCollection<InterfaceTheme> AvailableInterfaceThemes { get; } = [];
    public ObservableCollection<EditorTheme>    AvailableEditorThemes    { get; } = [];

    public PreferencesWindowViewModel(IThemeService themeService, IPreferencesService prefs)
    {
        _themeService = themeService;
        _prefs        = prefs;

        // Initialize directly — no bindings exist yet, so no RaisePropertyChanged needed
        var theme = themeService.CurrentTheme;
        _selectedInterfaceTheme = theme.Interface;
        _selectedEditorTheme    = theme.Editor;
        foreach (var t in themeService.InterfaceThemes.Where(t => t.IsDark == theme.Interface.IsDark))
            AvailableInterfaceThemes.Add(t);
        foreach (var t in themeService.EditorThemes.Where(t => t.IsDark == theme.Editor.IsDark))
            AvailableEditorThemes.Add(t);

        themeService.ThemeChanged += OnThemeChanged;
    }

    public void Dispose() => _themeService.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(ActiveTheme theme) => RefreshThemeLists(theme);

    private void RefreshThemeLists(ActiveTheme theme)
    {
        // Only rebuild lists when dark/light polarity flips — avoids ComboBox flicker on
        // simple theme-within-same-mode changes
        if (AvailableInterfaceThemes.Count == 0 ||
            AvailableInterfaceThemes[0].IsDark != theme.Interface.IsDark)
        {
            AvailableInterfaceThemes.Clear();
            foreach (var t in _themeService.InterfaceThemes.Where(t => t.IsDark == theme.Interface.IsDark))
                AvailableInterfaceThemes.Add(t);
        }

        if (AvailableEditorThemes.Count == 0 ||
            AvailableEditorThemes[0].IsDark != theme.Editor.IsDark)
        {
            AvailableEditorThemes.Clear();
            foreach (var t in _themeService.EditorThemes.Where(t => t.IsDark == theme.Editor.IsDark))
                AvailableEditorThemes.Add(t);
        }

        _selectedInterfaceTheme = theme.Interface;
        this.RaisePropertyChanged(nameof(SelectedInterfaceTheme));
        _selectedEditorTheme = theme.Editor;
        this.RaisePropertyChanged(nameof(SelectedEditorTheme));
    }

    // ── Appearance mode ───────────────────────────────────────────────────────

    public ThemeMode SelectedMode
    {
        get => _prefs.Appearance.Mode;
        set
        {
            if (_prefs.Appearance.Mode == value) return;
            _themeService.SetMode(value);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsSystemMode));
            this.RaisePropertyChanged(nameof(IsDarkMode));
            this.RaisePropertyChanged(nameof(IsLightMode));
        }
    }

    public bool IsSystemMode { get => SelectedMode == ThemeMode.System; set { if (value) SelectedMode = ThemeMode.System; } }
    public bool IsDarkMode   { get => SelectedMode == ThemeMode.Dark;   set { if (value) SelectedMode = ThemeMode.Dark;   } }
    public bool IsLightMode  { get => SelectedMode == ThemeMode.Light;  set { if (value) SelectedMode = ThemeMode.Light;  } }

    // ── Interface theme ───────────────────────────────────────────────────────

    public InterfaceTheme? SelectedInterfaceTheme
    {
        get => _selectedInterfaceTheme;
        set
        {
            // Guard prevents re-triggering SetInterfaceTheme when ComboBox writes back
            // the same value after RefreshThemeLists updates the backing field
            if (_selectedInterfaceTheme == value) return;
            this.RaiseAndSetIfChanged(ref _selectedInterfaceTheme, value);
            if (value is not null) _themeService.SetInterfaceTheme(value);
        }
    }

    // ── Editor theme ──────────────────────────────────────────────────────────

    public EditorTheme? SelectedEditorTheme
    {
        get => _selectedEditorTheme;
        set
        {
            if (_selectedEditorTheme == value) return;
            this.RaiseAndSetIfChanged(ref _selectedEditorTheme, value);
            if (value is not null) _themeService.SetEditorTheme(value);
        }
    }

    // ── Editor settings ───────────────────────────────────────────────────────

    public bool ShowLineNumbers
    {
        get => _prefs.Editor.ShowLineNumbers;
        set { _prefs.Editor.ShowLineNumbers = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public bool ShowTabs
    {
        get => _prefs.Editor.ShowTabs;
        set { _prefs.Editor.ShowTabs = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public bool ShowSpaces
    {
        get => _prefs.Editor.ShowSpaces;
        set { _prefs.Editor.ShowSpaces = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public bool ShowEndOfLine
    {
        get => _prefs.Editor.ShowEndOfLine;
        set { _prefs.Editor.ShowEndOfLine = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public bool ShowColumnRuler
    {
        get => _prefs.Editor.ShowColumnRuler;
        set { _prefs.Editor.ShowColumnRuler = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public bool WordWrap
    {
        get => _prefs.Editor.WordWrap;
        set { _prefs.Editor.WordWrap = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public double FontSize
    {
        get => _prefs.Editor.FontSize;
        set { _prefs.Editor.FontSize = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    // ── Debugger ──────────────────────────────────────────────────────────────

    public string DotnetPath
    {
        get => _prefs.Debugger.DotnetPath;
        set { _prefs.Debugger.DotnetPath = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }

    public string DebuggerPath
    {
        get => _prefs.Debugger.DebuggerPath;
        set { _prefs.Debugger.DebuggerPath = value; _prefs.Save(); this.RaisePropertyChanged(); }
    }
}
