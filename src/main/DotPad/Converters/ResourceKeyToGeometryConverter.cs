using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DotPad.Converters;

// Resolves a resource key string (FileTreeViewNode.LanguageIconKey) to the actual Geometry it names
// in Resources/Icons.axaml, at bind time — so a language with no icon (LanguageIconKey null) just
// produces a null Geometry, no per-language XAML branch needed for it. Same TryGetResource(key,
// null, ...) pattern CodeFixMargin already uses for its own (non-bound, code-behind) icon lookup.
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public static readonly ResourceKeyToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;
        return Application.Current?.TryGetResource(key, null, out var resource) == true ? resource : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
