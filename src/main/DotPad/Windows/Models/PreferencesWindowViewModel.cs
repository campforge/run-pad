using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using DotPad.Avalonia.Themes;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class PreferencesWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly IPreferencesService _prefs;

    private InterfaceTheme? _selectedInterfaceTheme;
    private EditorTheme? _selectedEditorTheme;

    public ObservableCollection<InterfaceTheme> AvailableInterfaceThemes { get; } = [];
    public ObservableCollection<EditorTheme> AvailableEditorThemes { get; } = [];

    public PreferencesWindowViewModel(IThemeService themeService, IPreferencesService prefs)
    {
        _themeService = themeService;
        _prefs = prefs;

        // Initialize directly — no bindings exist yet, so no RaisePropertyChanged needed
        var theme = themeService.CurrentTheme;
        _selectedInterfaceTheme = theme.Interface;
        _selectedEditorTheme = theme.Editor;
        foreach (var t in themeService.InterfaceThemes.Where(t => t.IsDark == theme.Interface.IsDark))
            AvailableInterfaceThemes.Add(t);
        foreach (var t in themeService.EditorThemes.Where(t => t.IsDark == theme.Editor.IsDark))
            AvailableEditorThemes.Add(t);

        themeService.ThemeChanged += OnThemeChanged;
    }

    public void Dispose()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        GC.SuppressFinalize(this);
    }

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
        set => SetPref(_prefs.Editor.ShowLineNumbers, value, v => _prefs.Editor.ShowLineNumbers = v);
    }

    public bool ShowFolding
    {
        get => _prefs.Editor.ShowFolding;
        set => SetPref(_prefs.Editor.ShowFolding, value, v => _prefs.Editor.ShowFolding = v);
    }

    public bool ShowTabs
    {
        get => _prefs.Editor.ShowTabs;
        set => SetPref(_prefs.Editor.ShowTabs, value, v => _prefs.Editor.ShowTabs = v);
    }

    public bool ShowSpaces
    {
        get => _prefs.Editor.ShowSpaces;
        set => SetPref(_prefs.Editor.ShowSpaces, value, v => _prefs.Editor.ShowSpaces = v);
    }

    public bool ShowEndOfLine
    {
        get => _prefs.Editor.ShowEndOfLine;
        set => SetPref(_prefs.Editor.ShowEndOfLine, value, v => _prefs.Editor.ShowEndOfLine = v);
    }

    public bool ShowColumnRuler
    {
        get => _prefs.Editor.ShowColumnRuler;
        set => SetPref(_prefs.Editor.ShowColumnRuler, value, v => _prefs.Editor.ShowColumnRuler = v);
    }

    public bool WordWrap
    {
        get => _prefs.Editor.WordWrap;
        set => SetPref(_prefs.Editor.WordWrap, value, v => _prefs.Editor.WordWrap = v);
    }

    public double FontSize
    {
        get => _prefs.Editor.FontSize;
        set => SetPref(_prefs.Editor.FontSize, value, v => _prefs.Editor.FontSize = v);
    }

    // ── Debugger ──────────────────────────────────────────────────────────────

    public string DotnetPath
    {
        get => _prefs.Debugger.DotnetPath;
        set => SetPref(_prefs.Debugger.DotnetPath, value, v => _prefs.Debugger.DotnetPath = v);
    }

    public string DebuggerPath
    {
        get => _prefs.Debugger.DebuggerPath;
        set => SetPref(_prefs.Debugger.DebuggerPath, value, v => _prefs.Debugger.DebuggerPath = v);
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    public string HotkeyRun
    {
        get => _prefs.Keyboard.Run;
        set => SetPref(_prefs.Keyboard.Run, value, v => _prefs.Keyboard.Run = v);
    }

    public string HotkeyCancel
    {
        get => _prefs.Keyboard.Cancel;
        set => SetPref(_prefs.Keyboard.Cancel, value, v => _prefs.Keyboard.Cancel = v);
    }

    public string HotkeyStepOver
    {
        get => _prefs.Keyboard.StepOver;
        set => SetPref(_prefs.Keyboard.StepOver, value, v => _prefs.Keyboard.StepOver = v);
    }

    public string HotkeyStepIn
    {
        get => _prefs.Keyboard.StepIn;
        set => SetPref(_prefs.Keyboard.StepIn, value, v => _prefs.Keyboard.StepIn = v);
    }

    public string HotkeyStepOut
    {
        get => _prefs.Keyboard.StepOut;
        set => SetPref(_prefs.Keyboard.StepOut, value, v => _prefs.Keyboard.StepOut = v);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private void SetPref<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        _prefs.Save();
        this.RaisePropertyChanged(name);
    }
}
