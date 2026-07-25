using Avalonia.Styling;

namespace RunPad.Avalonia.Themes;

public static class RunPadThemeVariants
{
    // Inherit from Dark/Light so Fluent controls get the right base variant
    // when ThemeDictionary doesn't have an exact key match.
    public static readonly ThemeVariant VsCodeDark = new("VsCodeDark", ThemeVariant.Dark);
    public static readonly ThemeVariant VsCodeLight = new("VsCodeLight", ThemeVariant.Light);
    public static readonly ThemeVariant RiderDark = new("RiderDark", ThemeVariant.Dark);
    public static readonly ThemeVariant RiderLight = new("RiderLight", ThemeVariant.Light);
    public static readonly ThemeVariant Vs2022Dark = new("Vs2022Dark", ThemeVariant.Dark);
    public static readonly ThemeVariant Vs2022Light = new("Vs2022Light", ThemeVariant.Light);
    public static readonly ThemeVariant RunPadMidnight = new("RunPadMidnight", ThemeVariant.Dark);
    public static readonly ThemeVariant RunPadWarmLight = new("RunPadWarmLight", ThemeVariant.Light);
}
