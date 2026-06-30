using System.Configuration;
using Avalonia;
using Avalonia.Platform;
using DotPad.Avalonia.Themes;
using DotPad.Models;
using TextMateSharp.Grammars;

namespace DotPad.Services;

public delegate void ThemeChangeDelegate(Theme theme);

public interface IThemeService
{
    IReadOnlyList<Theme> Themes { get; }
    void SetTheme(Theme theme);
    Theme CurrentTheme { get; }
    event ThemeChangeDelegate? ThemeChange;
}

public sealed class ThemeService : IThemeService
{
    private static readonly IReadOnlyList<Theme> AllThemes =
    [
        new("VS Code Dark+",  ThemeName.DarkPlus, DotPadThemeVariants.VsCodeDark),
        new("VS Code Light+", ThemeName.Light,    DotPadThemeVariants.VsCodeLight),
        new("Rider Dark",     ThemeName.DarkPlus, DotPadThemeVariants.RiderDark),
        new("Rider Light",    ThemeName.Light,    DotPadThemeVariants.RiderLight),
    ];

    private readonly Application _app;
    private Theme _currentTheme = AllThemes[0];

    public ThemeService(Application app)
    {
        _app = app;
        var raw = ConfigurationManager.AppSettings["Theme"];
        int index;
        if (raw != null && int.TryParse(raw, out var saved))
        {
            index = Math.Clamp(saved, 0, AllThemes.Count - 1);
        }
        else
        {
            var systemVariant = app.PlatformSettings?.GetColorValues().ThemeVariant;
            index = systemVariant == PlatformThemeVariant.Light ? 1 : 0;
        }
        SetTheme(AllThemes[index]);
    }

    public IReadOnlyList<Theme> Themes => AllThemes;
    public Theme CurrentTheme => _currentTheme;

    public event ThemeChangeDelegate? ThemeChange;

    public void SetTheme(Theme theme)
    {
        _currentTheme = theme;
        _app.RequestedThemeVariant = theme.WindowTheme;
        ThemeChange?.Invoke(theme);
    }
}
