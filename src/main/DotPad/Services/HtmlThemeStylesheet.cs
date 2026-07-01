using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DotPad.Services;

public static class HtmlThemeStylesheet
{
    public static string Generate(ThemeVariant variant)
    {
        var bg      = GetColor("DotPad.Output.BackgroundBrush",      variant, "#1E1E1E");
        var fg      = GetColor("DotPad.Text.PrimaryBrush",           variant, "#CCCCCC");
        var fgSec   = GetColor("DotPad.Text.SecondaryBrush",         variant, "#9E9E9E");
        var panel   = GetColor("DotPad.Panel.BackgroundBrush",       variant, "#252526");
        var errFg   = GetColor("DotPad.Output.ErrorForeground",      variant, "#F14C4C");
        var stackFg = GetColor("DotPad.Output.StackTraceForeground", variant, "#CD9591");

        return $$"""
            body       { background-color:{{bg}}; color:{{fg}}; font-family:Consolas,monospace; font-size:13px; margin:8px; }
            pre        { background-color:{{panel}}; color:{{fg}}; padding:4px 8px; margin:4px 0; }
            .secondary { color:{{fgSec}}; }
            .error     { color:{{errFg}}; }
            .stacktrace{ color:{{stackFg}}; font-size:0.85em; }
            """;
    }

    private static string GetColor(string key, ThemeVariant variant, string fallback)
    {
        if (Application.Current?.TryGetResource(key, variant, out var res) == true
            && res is ISolidColorBrush brush)
            return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        return fallback;
    }
}
