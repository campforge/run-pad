using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using Avalonia;
using Avalonia.Styling;
using DotPad.Models;
using TextMateSharp.Grammars;

namespace DotPad.Services;

public delegate void ThemeChangeDelegate(Theme theme);

public interface IThemeService
{
    public IList<Theme> Themes { get; }
    public Theme GetTheme(ThemeType type);
    void SetTheme(Theme theme);
    Theme CurrentTheme { get; }

    public event ThemeChangeDelegate? ThemeChange;
}

internal sealed class ThemeService : IThemeService
{
    private static IThemeService _instanse;
    private Application _app;
    private static readonly object _lock = new();

    private static readonly Dictionary<ThemeType, Theme> ThemesDictionary = new()
    {
        {
            ThemeType.Dark,
            new Theme(ThemeName.DarkPlus, ThemeVariant.Dark, ThemeType.Dark, "White")
        },
        {
            ThemeType.Light,
            new Theme(ThemeName.Light, ThemeVariant.Light, ThemeType.Light, "Black")
        },
        {
            ThemeType.Default,
            new Theme(ThemeName.Light, ThemeVariant.Default, ThemeType.Default, ThemeType.Default.ToString())
        },
    };

    private static Theme _currentTheme = ThemesDictionary[ThemeType.Dark];

    static ThemeService()
    {
    }

    private ThemeService(Application app)
    {
        _app = app;
        var theme = ConfigurationManager.AppSettings["Theme"];
        var appSettingsTheme = 0;
        if (theme != null)
        {
            int.TryParse(theme, out appSettingsTheme);
        }
        SetTheme(GetTheme((ThemeType)appSettingsTheme));
    }

    public IList<Theme> Themes => ThemesDictionary.Values.ToList();

    public Theme GetTheme(ThemeType type)
    {
        return ThemesDictionary[type];
    }

    public Theme CurrentTheme => _currentTheme;

    public event ThemeChangeDelegate? ThemeChange;

    public void SetTheme(Theme theme)
    {
        _currentTheme = theme;
        _app.RequestedThemeVariant = theme.WindowTheme;
        ThemeChange?.Invoke(theme);
    }

    public static IThemeService GetInstance()
    {
        return _instanse;
    }

    public static void CreateInstance(Application app)
    {
        lock (_lock)
        {
            _instanse = new ThemeService(app);
        }
    }
}