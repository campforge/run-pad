using DotPad.Models;
using DotPad.Services;
using ReactiveUI;

namespace DotPad.ViewModels;

public class PreferencesWindowViewModel(IThemeService themeService, IOptionsService optionsService) : ViewModelBase
{
    private Theme _selectedTheme = themeService.CurrentTheme;
    private bool _showTabs;
    private bool _showSpaces;
    private bool _showEndOfLine;
    private bool _showColumnRulers;

    public IReadOnlyList<Theme> Themes => themeService.Themes;

    public Theme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            if (value is not null)
                themeService.SetTheme(value);
        }
    }

    public bool ShowTabs
    {
        get => _showTabs;
        set => this.RaiseAndSetIfChanged(ref _showTabs, value);
    }

    public bool ShowSpaces
    {
        get => _showSpaces;
        set => this.RaiseAndSetIfChanged(ref _showSpaces, value);
    }

    public bool ShowEndOfLine
    {
        get => _showEndOfLine;
        set => this.RaiseAndSetIfChanged(ref _showEndOfLine, value);
    }

    public bool ShowColumnRulers
    {
        get => _showColumnRulers;
        set => this.RaiseAndSetIfChanged(ref _showColumnRulers, value);
    }

    public string DotnetPath
    {
        get => optionsService.DotnetPath;
        set
        {
            optionsService.DotnetPath = value;
            this.RaisePropertyChanged();
        }
    }

    public string DebuggerPath
    {
        get => optionsService.DebuggerPath;
        set
        {
            optionsService.DebuggerPath = value;
            this.RaisePropertyChanged();
        }
    }
}
