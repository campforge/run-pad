using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Invoklet.Avalonia.Themes;

public static class HtmlThemeStylesheet
{
    /// <summary>
    /// Builds the stylesheet for the results pane. With <paramref name="wrapLongLines"/> script output
    /// (lines in <c>.out</c> and <c>.error</c>) and preformatted text break at the viewport width, even
    /// inside one unbroken token; without it a long unbroken line scrolls sideways. Spaces and explicit
    /// line breaks are kept. Dump tables keep their own layout.
    /// </summary>
    public static string Generate(ThemeVariant variant, bool isDark, bool wrapLongLines = false)
    {
        var bg = GetColor("Invoklet.Output.BackgroundBrush", variant, isDark ? "#1E1E1E" : "#FFFFFF");
        var fg = GetColor("Invoklet.Text.PrimaryBrush", variant, isDark ? "#CCCCCC" : "#383838");
        var fgSec = GetColor("Invoklet.Text.SecondaryBrush", variant, isDark ? "#9E9E9E" : "#717171");
        var panel = GetColor("Invoklet.Panel.BackgroundBrush", variant, isDark ? "#252526" : "#F3F3F3");
        var errFg = GetColor("Invoklet.Output.ErrorForeground", variant, isDark ? "#F14C4C" : "#CC0000");
        var stackFg = GetColor("Invoklet.Output.StackTraceForeground", variant, isDark ? "#CD9591" : "#A31515");

        var dpTh = GetColor("Invoklet.Dump.TableHeaderBrush", variant, isDark ? "#1B4F7D" : "#3A6DAD");
        var dpThFg = GetColor("Invoklet.Dump.TableHeaderForegroundBrush", variant, "#FFFFFF");
        var dpBorder = GetColor("Invoklet.Dump.TableBorderBrush", variant, isDark ? "#3C3C3C" : "#A8B4C8");
        var dpEven = GetColor("Invoklet.Dump.TableEvenRowBrush", variant, isDark ? "#252525" : "#EDF2FB");
        var dpAccent = GetColor("Invoklet.Tab.AccentBrush", variant, isDark ? "#569CD6" : "#0078D4");
        var dpString = GetColor("Invoklet.Dump.StringBrush", variant, isDark ? "#CE9178" : "#A31515");
        var dpNumber = GetColor("Invoklet.Dump.NumberBrush", variant, isDark ? "#B5CEA8" : "#098658");
        var dpBool = GetColor("Invoklet.Dump.BoolBrush", variant, isDark ? "#569CD6" : "#0000CC");
        var dpEnum = GetColor("Invoklet.Dump.EnumBrush", variant, isDark ? "#4EC9B0" : "#267F99");
        var dpDate = GetColor("Invoklet.Dump.DateTimeBrush", variant, isDark ? "#C8A0D4" : "#7B3FA0");
        var dpProp = GetColor("Invoklet.Dump.PropertyNameBrush", variant, isDark ? "#9CDCFE" : "#0F4A8A");
        var dpDumpBg = isDark ? "rgba(255,255,255,0.02)" : "transparent";
        // A highlight is a marker pen: yellow with black text in every theme, so it reads the same on a dark and a light panel.
        var highlightBg = isDark ? "#E6C84A" : "#FFF176";
        const string highlightFg = "#000000";

        // The HTML renderer has no overflow-wrap, so break-all is what lets a single long token fit.
        var preWrap = wrapLongLines ? " white-space:pre-wrap; word-break:break-all;" : "";
        var lines = wrapLongLines ? "white-space:pre-wrap; word-break:break-all;" : "white-space:pre;";

        return $$"""
            body        { background-color:{{bg}}; color:{{fg}}; font-family:Consolas,monospace; font-size:13px; margin:8px; }
            pre         { background-color:{{panel}}; color:{{fg}}; padding:4px 8px; margin:4px 0;{{preWrap}} }
            .out        { {{lines}} }
            .secondary  { color:{{fgSec}}; }
            .error      { color:{{errFg}}; {{lines}} }
            .stacktrace { color:{{stackFg}}; font-size:0.85em; }
            .dp-dump    { margin:8px 0; padding:4px 6px 4px 10px; border-left:3px solid {{dpAccent}}; background-color:{{dpDumpBg}}; border-radius:0 2px 2px 0; }
            .dp-label   { color:{{fgSec}}; font-size:0.85em; margin-bottom:3px; font-style:italic; }
            .dp-heading { color:{{fg}}; font-weight:bold; margin-bottom:3px; }
            .dp-highlight { background-color:{{highlightBg}}; color:{{highlightFg}}; padding:0 3px; }
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
            .dp-progressbar        { position:relative; width:240px; height:14px; background-color:{{dpBorder}}; border-radius:3px; overflow:hidden; margin:2px 0; }
            .dp-progressbar-fill   { height:100%; background-color:{{dpAccent}}; }
            .dp-progressbar-caption{ color:{{fgSec}}; font-size:0.85em; margin-bottom:2px; }
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
