using TextMateSharp.Grammars;

namespace DotPad.Assets.Models;

public sealed record EditorTheme(string Name, ThemeName TextMateName, bool IsDark);
