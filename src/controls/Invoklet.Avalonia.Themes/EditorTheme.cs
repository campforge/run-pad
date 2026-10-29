using TextMateSharp.Grammars;

namespace Invoklet.Avalonia.Themes;

public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
