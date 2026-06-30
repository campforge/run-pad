using Avalonia.Styling;

namespace DotPad.Avalonia.Themes;

public static class DotPadThemeVariants
{
    // Inherit from Dark/Light so Fluent controls get the right base variant
    // when ThemeDictionary doesn't have an exact key match.
    public static readonly ThemeVariant VsCodeDark  = new("VsCodeDark",  ThemeVariant.Dark);
    public static readonly ThemeVariant VsCodeLight = new("VsCodeLight", ThemeVariant.Light);
    public static readonly ThemeVariant RiderDark   = new("RiderDark",   ThemeVariant.Dark);
    public static readonly ThemeVariant RiderLight  = new("RiderLight",  ThemeVariant.Light);
}
