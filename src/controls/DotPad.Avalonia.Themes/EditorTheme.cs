using TextMateSharp.Grammars;

namespace DotPad.Avalonia.Themes;

public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
