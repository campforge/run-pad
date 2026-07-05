using Avalonia.Styling;

namespace DotPad.Assets.Models;

// ToDo: перенести в DotPad.Avalonia.Themes
public sealed record InterfaceTheme(string Name, ThemeVariant Variant, bool IsDark);
