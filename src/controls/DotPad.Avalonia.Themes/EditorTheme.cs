using TextMateSharp.Grammars;

namespace DotPad.Assets.Models;

// ToDo: перенести в DotPad.Avalonia.Themes
public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
