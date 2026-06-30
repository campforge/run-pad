using Avalonia.Styling;
using TextMateSharp.Grammars;

namespace DotPad.Models;

public record Theme(
    string Name,
    ThemeName CodeEditorThemeName,
    ThemeVariant WindowTheme);
