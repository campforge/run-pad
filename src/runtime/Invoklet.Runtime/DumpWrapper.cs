namespace Invoklet.Runtime;

/// <summary>
/// A value together with how its output is dressed: a CSS class and inline style around it (<c>Util.Highlight</c>, <c>Util.WithStyle</c>),
/// or a heading above it (<c>Util.WithHeading</c>). The renderer looks through it at the same depth, so wrapping a value never
/// changes how deep the value is dumped; printed as text it is the value itself.
/// </summary>
internal sealed class DumpWrapper
{
    private DumpWrapper(object? value) => Value = value;

    public object? Value { get; }

    /// <summary>The CSS class of the element around the value; null when the wrapper only adds a heading.</summary>
    public string? CssClass { get; private set; }

    /// <summary>The value of the <c>style</c> attribute around it; null for none.</summary>
    public string? Style { get; private set; }

    public bool HasHeading { get; private set; }

    public object? Heading { get; private set; }

    public static DumpWrapper Highlighted(object? value, string? color) => new(value)
    {
        CssClass = "dp-highlight",
        Style = string.IsNullOrWhiteSpace(color) ? null : "background-color:" + color!.Trim(),
    };

    public static DumpWrapper Styled(object? value, string? css) => new(value)
    {
        CssClass = "dp-styled",
        Style = string.IsNullOrWhiteSpace(css) ? null : css!.Trim(),
    };

    /// <summary>A class of the script's own (see <c>Util.HtmlHead.AddStyles</c>); only letters, digits, '-' and '_' are kept, in classes split by spaces.</summary>
    public static DumpWrapper WithCssClass(object? value, string? cssClass) => new(value)
    {
        CssClass = string.Join(" ", (cssClass ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(c => new string(c.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray()))
            .Where(c => c.Length > 0)) is { Length: > 0 } classes ? classes : "dp-styled",
    };

    public static DumpWrapper WithHeading(object? value, object? heading) => new(value) { HasHeading = true, Heading = heading };

    public override string? ToString() => Value?.ToString();
}
