using System.Configuration;
using Avalonia;
using Avalonia.Styling;
using DotPad.Models;
using TextMateSharp.Grammars;

namespace DotPad.Services;

public delegate void ThemeChangeDelegate(Theme theme);

public interface IThemeService
{
    IList<Theme> Themes { get; }
    Theme GetTheme(ThemeType type);
    void SetTheme(Theme theme);
    Theme CurrentTheme { get; }
    event ThemeChangeDelegate? ThemeChange;
}

public sealed class ThemeService : IThemeService
{
    private static readonly Dictionary<ThemeType, Theme> ThemesDictionary = new()
    {
        { ThemeType.Dark,    new Theme(ThemeName.DarkPlus, ThemeVariant.Dark,    ThemeType.Dark,    "White") },
        { ThemeType.Light,   new Theme(ThemeName.Light,    ThemeVariant.Light,   ThemeType.Light,   "Black") },
        { ThemeType.Default, new Theme(ThemeName.Light,    ThemeVariant.Default, ThemeType.Default, ThemeType.Default.ToString()) },
    };

    private readonly Application _app;
    private Theme _currentTheme = ThemesDictionary[ThemeType.Dark];

    public ThemeService(Application app)
    {
        _app = app;
        var raw = ConfigurationManager.AppSettings["Theme"];
        var themeIndex = raw != null && int.TryParse(raw, out var i) ? i : 0;
        SetTheme(GetTheme((ThemeType)themeIndex));
    }

    public IList<Theme> Themes => ThemesDictionary.Values.ToList();
    public Theme GetTheme(ThemeType type) => ThemesDictionary[type];
    public Theme CurrentTheme => _currentTheme;

    public event ThemeChangeDelegate? ThemeChange;

    public void SetTheme(Theme theme)
    {
        _currentTheme = theme;
        _app.RequestedThemeVariant = theme.WindowTheme;
        ThemeChange?.Invoke(theme);
    }
}
