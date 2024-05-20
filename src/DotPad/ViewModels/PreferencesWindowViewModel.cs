using System.Collections.Generic;
using System.Linq;
using DotPad.Models;
using DotPad.Services;

namespace DotPad.ViewModels;

internal class PreferencesWindowViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    private ThemeType _selectedTheme;

    public List<Theme> Themes => _themeService.Themes.ToList();
    public bool ShowTabs { get; set; }
    public bool ShowSpaces { get; set; }
    public bool ShowEndOfLine { get; set; }
    public bool ShowColumnRulers { get; set; }

    public ThemeType? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!value.HasValue)
                return;

            var theme = _themeService.GetTheme(value.Value);
            _themeService.SetTheme(theme);
            _selectedTheme = value.Value;
        }
    }

    public PreferencesWindowViewModel()
    {
        _themeService = ThemeService.GetInstance();
        _selectedTheme = _themeService.CurrentTheme.ThemeType;
    }

}