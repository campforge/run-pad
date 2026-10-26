using Avalonia;
using Avalonia.Data.Converters;

namespace RunPad.Converters;

/// <summary>Converters for the sidebar trees' row template (Resources/NavigationStyles.axaml).</summary>
public static class TreeRowConverters
{
    /// <summary>Left indent per depth level (TreeViewItem.Level), in pixels.</summary>
    public const double IndentStep = 12;

    public static readonly IValueConverter Indent =
        new FuncValueConverter<int, Thickness>(level => new Thickness(Math.Max(0, level) * IndentStep, 0, 0, 0));

    /// <summary>Whether a row has children (ItemCount), and so a disclosure chevron.</summary>
    public static readonly IValueConverter HasItems = new FuncValueConverter<int, bool>(count => count > 0);
}
