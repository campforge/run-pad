using Avalonia.Styling;
using TextMateSharp.Grammars;

namespace DotPad.Models;

public enum ThemeType
{
    Default = 0,
    Dark = 1,
    Light = 2,
}

public record Theme(
    ThemeName CodeEditorThemeName, 
    ThemeVariant WindowTheme, 
    ThemeType ThemeType,
    string Description);