using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DotPad.Services;

// ToDo: перенести в DotPad.Avalonia.Themes
public static class HtmlThemeStylesheet
{
    public static string Generate(ThemeVariant variant, bool isDark)
    {
        var bg = GetColor("DotPad.Output.BackgroundBrush", variant, isDark ? "#1E1E1E" : "#FFFFFF");
        var fg = GetColor("DotPad.Text.PrimaryBrush", variant, isDark ? "#CCCCCC" : "#383838");
        var fgSec = GetColor("DotPad.Text.SecondaryBrush", variant, isDark ? "#9E9E9E" : "#717171");
        var panel = GetColor("DotPad.Panel.BackgroundBrush", variant, isDark ? "#252526" : "#F3F3F3");
        var errFg = GetColor("DotPad.Output.ErrorForeground", variant, isDark ? "#F14C4C" : "#CC0000");
        var stackFg = GetColor("DotPad.Output.StackTraceForeground", variant, isDark ? "#CD9591" : "#A31515");

        var dpTh = GetColor("DotPad.Dump.TableHeaderBrush", variant, isDark ? "#1B4F7D" : "#3A6DAD");
        var dpThFg = GetColor("DotPad.Dump.TableHeaderForegroundBrush", variant, "#FFFFFF");
        var dpBorder = GetColor("DotPad.Dump.TableBorderBrush", variant, isDark ? "#3C3C3C" : "#A8B4C8");
        var dpEven = GetColor("DotPad.Dump.TableEvenRowBrush", variant, isDark ? "#252525" : "#EDF2FB");
        var dpAccent = GetColor("DotPad.Tab.AccentBrush", variant, isDark ? "#569CD6" : "#0078D4");
        var dpString = GetColor("DotPad.Dump.StringBrush", variant, isDark ? "#CE9178" : "#A31515");
        var dpNumber = GetColor("DotPad.Dump.NumberBrush", variant, isDark ? "#B5CEA8" : "#098658");
        var dpBool = GetColor("DotPad.Dump.BoolBrush", variant, isDark ? "#569CD6" : "#0000CC");
        var dpEnum = GetColor("DotPad.Dump.EnumBrush", variant, isDark ? "#4EC9B0" : "#267F99");
        var dpDate = GetColor("DotPad.Dump.DateTimeBrush", variant, isDark ? "#C8A0D4" : "#7B3FA0");
        var dpProp = GetColor("DotPad.Dump.PropertyNameBrush", variant, isDark ? "#9CDCFE" : "#0F4A8A");
        var dpDumpBg = isDark ? "rgba(255,255,255,0.02)" : "transparent";

        return $$"""
            body        { background-color:{{bg}}; color:{{fg}}; font-family:Consolas,monospace; font-size:13px; margin:8px; }
            pre         { background-color:{{panel}}; color:{{fg}}; padding:4px 8px; margin:4px 0; }
            .secondary  { color:{{fgSec}}; }
            .error      { color:{{errFg}}; }
            .stacktrace { color:{{stackFg}}; font-size:0.85em; }
            .dp-dump    { margin:8px 0; padding:4px 6px 4px 10px; border-left:3px solid {{dpAccent}}; background-color:{{dpDumpBg}}; border-radius:0 2px 2px 0; }
            .dp-label   { color:{{fgSec}}; font-size:0.85em; margin-bottom:3px; font-style:italic; }
            .dp-type    { color:{{fgSec}}; font-size:0.82em; margin-bottom:2px; }
            .dp-count   { color:{{fgSec}}; font-size:0.82em; margin-bottom:4px; }
            .dp-table   { border-collapse:collapse; font-size:12px; margin:2px 0; border:1px solid {{dpBorder}}; }
            .dp-th      { background-color:{{dpTh}}; color:{{dpThFg}}; padding:3px 10px; text-align:left; border:1px solid {{dpBorder}}; border-bottom:2px solid {{dpBorder}}; font-weight:600; white-space:nowrap; }
            .dp-td      { padding:2px 10px; border:1px solid {{dpBorder}}; vertical-align:top; }
            .dp-table tbody tr:nth-child(even) td { background-color:{{dpEven}}; }
            .dp-propname{ color:{{dpProp}}; }
            .dp-string  { color:{{dpString}}; }
            .dp-number  { color:{{dpNumber}}; }
            .dp-bool    { color:{{dpBool}}; }
            .dp-null    { color:#808080; font-style:italic; }
            .dp-enum    { color:{{dpEnum}}; }
            .dp-datetime{ color:{{dpDate}}; }
            .dp-error   { color:{{errFg}}; }
            .dp-truncated{ color:{{fgSec}}; }
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
