using Invoklet.Runtime;

// ReSharper disable once CheckNamespace
// Util lives in the global namespace, like LINQPad's, so that a script can say Util.Highlight without a using.

// LINQPad-compatible ways to dress a value in the output of Dump: the wrappers are values themselves, so they can be dumped alone
// (Util.Highlight(x).Dump()) or put inside what is dumped (new { Total = Util.HighlightIf(total, t => t > 100) }.Dump()).
public static partial class Util
{
    /// <summary>
    /// Wraps a value so that it shows with a highlight (yellow unless <paramref name="color"/> says another CSS colour) when it is dumped —
    /// LINQPad-compatible <c>Util.Highlight</c>. Printed as text, the result is the value itself.
    /// </summary>
    public static object Highlight<T>(T data, string? color = null) => DumpWrapper.Highlighted(data, color);

    /// <summary>Highlights <paramref name="data"/> only when <paramref name="predicate"/> is true for it; otherwise returns it as it is.</summary>
    public static object? HighlightIf<T>(T data, Func<T, bool> predicate, string? color = null)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return predicate(data) ? DumpWrapper.Highlighted(data, color) : data;
    }

    /// <summary>Wraps a value so that it is dumped with the given CSS declarations (<c>"color:red; font-weight:bold"</c>) — LINQPad-compatible <c>Util.WithStyle</c>.</summary>
    public static object WithStyle<T>(T data, string css) => DumpWrapper.Styled(data, css);

    /// <summary>Wraps a value so that it is dumped under a heading — LINQPad-compatible <c>Util.WithHeading</c>.</summary>
    public static object WithHeading<T>(T data, object? heading) => DumpWrapper.WithHeading(data, heading);
}
