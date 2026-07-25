using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using RunPad.Avalonia.Themes;
using RunPad.Services.Preferences;
using TextMateSharp.Grammars;

namespace RunPad.Services;

public delegate void ThemeChangedDelegate(ActiveTheme theme);

public interface IThemeService
{
    IReadOnlyList<InterfaceTheme> InterfaceThemes { get; }
    IReadOnlyList<EditorTheme> EditorThemes { get; }

    ActiveTheme CurrentTheme { get; }

    void SetMode(ThemeMode mode);
    void SetInterfaceTheme(InterfaceTheme theme);
    void SetEditorTheme(EditorTheme theme);

    IBrush? GetBrush(string key);

    event ThemeChangedDelegate? ThemeChanged;
}

public sealed class ThemeService : IThemeService, IDisposable
{
    // ── Catalog ───────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<InterfaceTheme> AllInterfaceThemes =
    [
        new("VS Code Dark",  RunPadThemeVariants.VsCodeDark,  true),
        new("Rider Dark",    RunPadThemeVariants.RiderDark,   true),
        new("VS 2022 Dark",  RunPadThemeVariants.Vs2022Dark,  true),
        new("RunPad Midnight",   RunPadThemeVariants.RunPadMidnight,   true),
        new("VS Code Light",     RunPadThemeVariants.VsCodeLight,      false),
        new("Rider Light",       RunPadThemeVariants.RiderLight,       false),
        new("VS 2022 Light",     RunPadThemeVariants.Vs2022Light,      false),
        new("RunPad Warm Light", RunPadThemeVariants.RunPadWarmLight,  false),
    ];

    private static readonly IReadOnlyList<EditorTheme> AllEditorThemes =
    [
        // Dark
        new("Dark+",                ThemeName.DarkPlus,          true),
        new("Monokai",              ThemeName.Monokai,           true),
        new("Abyss",                ThemeName.Abbys,             true),
        new("Kimbie Dark",          ThemeName.KimbieDark,        true),
        new("Tomorrow Night Blue",  ThemeName.TomorrowNightBlue, true),
        new("High Contrast Dark",   ThemeName.HighContrastDark,  true),
        new("Solarized Dark",       ThemeName.SolarizedDark,     true),
        new("Atom One Dark",        ThemeName.AtomOneDark,       true),
        new("Visual Studio Dark",   ThemeName.VisualStudioDark,  true),
        // Light
        new("Light+",               ThemeName.Light,             false),
        new("Quiet Light",          ThemeName.QuietLight,        false),
        new("Solarized Light",      ThemeName.SolarizedLight,    false),
        new("Atom One Light",       ThemeName.AtomOneLight,      false),
        new("Visual Studio Light",  ThemeName.VisualStudioLight, false),
        new("High Contrast Light",  ThemeName.HighContrastLight, false),
    ];

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Application _app;
    private readonly IPreferencesService _prefs;
    private ActiveTheme _currentTheme;

    public ThemeService(Application app, IPreferencesService prefs)
    {
        _app = app;
        _prefs = prefs;

        if (app.PlatformSettings is { } ps)
            ps.ColorValuesChanged += OnColorValuesChanged;

        // Synchronous on startup — no UI input events in flight yet, and no ThemeChanged
        // subscribers exist before the constructor returns, so ApplyCore's own notification isn't
        // needed here — just resolve the initial theme directly.
        _currentTheme = ResolveCurrentTheme();
        _app.RequestedThemeVariant = _currentTheme.Interface.Variant;
    }

    // ── IThemeService ─────────────────────────────────────────────────────────

    public IReadOnlyList<InterfaceTheme> InterfaceThemes => AllInterfaceThemes;
    public IReadOnlyList<EditorTheme> EditorThemes => AllEditorThemes;
    public ActiveTheme CurrentTheme => _currentTheme;

    public event ThemeChangedDelegate? ThemeChanged;

    public void SetMode(ThemeMode mode)
    {
        _prefs.Appearance.Mode = mode;
        _prefs.Save();
        ScheduleApply();
    }

    public void SetInterfaceTheme(InterfaceTheme theme)
    {
        if (ResolveIsDark()) _prefs.Appearance.DarkInterfaceTheme = theme.Name;
        else _prefs.Appearance.LightInterfaceTheme = theme.Name;
        _prefs.Save();
        ScheduleApply();
    }

    public void SetEditorTheme(EditorTheme theme)
    {
        if (ResolveIsDark()) _prefs.Appearance.DarkEditorTheme = theme.Name;
        else _prefs.Appearance.LightEditorTheme = theme.Name;
        _prefs.Save();
        ScheduleApply();
    }

    public IBrush? GetBrush(string key)
    {
        object? val = null;
        _app.TryGetResource(key, _app.ActualThemeVariant, out val);
        return val as IBrush;
    }

    public void Dispose()
    {
        if (_app.PlatformSettings is { } ps)
            ps.ColorValuesChanged -= OnColorValuesChanged;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    // Post entire apply at Background priority (= 4).
    // All pointer/keyboard input events process at Input priority (= 5) which is HIGHER,
    // so they complete fully before we touch RequestedThemeVariant or fire ThemeChanged.
    // This eliminates "PlatformImpl is null" errors from InputManager.
    private void OnColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (_prefs.Appearance.Mode == ThemeMode.System)
            Dispatcher.UIThread.Post(ApplyCore, DispatcherPriority.Background);
    }

    private void ScheduleApply()
        => Dispatcher.UIThread.Post(ApplyCore, DispatcherPriority.Background);

    private void ApplyCore()
    {
        _currentTheme = ResolveCurrentTheme();
        _app.RequestedThemeVariant = _currentTheme.Interface.Variant;
        ThemeChanged?.Invoke(_currentTheme);
    }

    private ActiveTheme ResolveCurrentTheme()
    {
        bool isDark = ResolveIsDark();
        var a = _prefs.Appearance;

        var interfaceTheme = ResolveInterface(isDark ? a.DarkInterfaceTheme : a.LightInterfaceTheme, isDark);
        var editorTheme = ResolveEditor(isDark ? a.DarkEditorTheme : a.LightEditorTheme, isDark);
        return new ActiveTheme(interfaceTheme, editorTheme);
    }

    private bool ResolveIsDark() => _prefs.Appearance.Mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => _app.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light,
    };

    private static InterfaceTheme ResolveInterface(string name, bool isDark)
        => AllInterfaceThemes.FirstOrDefault(t => t.IsDark == isDark && t.Name == name)
           ?? AllInterfaceThemes.First(t => t.IsDark == isDark);

    private static EditorTheme ResolveEditor(string name, bool isDark)
        => AllEditorThemes.FirstOrDefault(t => t.IsDark == isDark && t.Name == name)
           ?? AllEditorThemes.First(t => t.IsDark == isDark);
}
