using TextMateSharp.Grammars;

namespace RunPad.Avalonia.Themes;

public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
