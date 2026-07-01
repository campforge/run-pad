using TextMateSharp.Grammars;

namespace DotPad.Models;

public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
