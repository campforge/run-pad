using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

// ReSharper disable once CheckNamespace

/// <summary>
/// The <c>.Dump()</c> extension every script calls to render a value to the output pane. Kept
/// call-compatible with LINQPad's original <c>ObjectExtensions</c>: <c>x.Dump()</c>,
/// <c>x.Dump("label")</c>, <c>x.Dump(3)</c> and <c>x.Dump("label", 3)</c> all work unchanged, so
/// scripts ported from LINQPad don't need to touch their <c>.Dump()</c> calls. DotPad-specific
/// rendering options (row limits, collapsing, member visibility, ...) are additional named
/// parameters on the same <see cref="Dump{T}(T, string?, int?, int?, int?, bool, int?, bool, bool, bool, bool, string?)"/>
/// overload rather than separate methods, so they don't collide with the LINQPad-shaped calls.
/// </summary>
public static class ObjectExtensions
{
    internal const char DumpLinePrefixChar = '\x1e'; // ASCII Record Separator — non-printable, won't appear in normal output

    /// <summary>
    /// Renders <paramref name="o"/> to the output pane and returns it unchanged, so a <c>.Dump()</c>
    /// call can be inserted inline without breaking an expression chain.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>; unconstrained, so this works on any value, including <see langword="null"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="label">
    /// Heading shown above the rendered value. When omitted, falls back to <paramref name="expression"/>
    /// — the source text of <paramref name="o"/> itself (e.g. <c>myList.Dump()</c> is headed "myList") —
    /// matching LINQPad's behavior of labeling an undescribed dump with the dumped expression.
    /// </param>
    /// <param name="maxDepth">How many levels of nested objects/collections to expand before truncating. LINQPad's own <c>Dump(int depth)</c> shape sets this positionally — see the <see cref="Dump{T}(T, int, string?)"/> overload.</param>
    /// <param name="maxRows">Maximum number of rows rendered for a collection/table before truncating.</param>
    /// <param name="collapseTo">If set, nested objects deeper than this level render collapsed (still expandable by clicking) instead of always expanded.</param>
    /// <param name="noTotals">Suppresses the row-count footer under a rendered table.</param>
    /// <param name="repeatHeadersAt">Repeats the table header every N rows, for long tables.</param>
    /// <param name="includePrivateMembers">Also renders non-public properties/fields.</param>
    /// <param name="alphabetizeMembers">Renders members alphabetically instead of in declaration order.</param>
    /// <param name="forceExpansion">Renders already expanded even for types that default to collapsed.</param>
    /// <param name="useInvariantCultureForNumbers">Formats numbers with <see cref="System.Globalization.CultureInfo.InvariantCulture"/> instead of the current culture.</param>
    /// <param name="expression">
    /// Supplied automatically by the compiler as the source text of <paramref name="o"/> — do not
    /// pass this explicitly; it exists only to power the <paramref name="label"/> fallback above.
    /// </param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    // Hidden from completion/signature help (CSharpIntelliSenseService checks this attribute) —
    // eleven parameters is unreadable in a signature-help popup. Calls still compile and run
    // identically; only the IDE hint disappears. The four LINQPad-shaped calls this overload also
    // serves (Dump(), Dump(label), Dump(label, depth)) lose their hint along with it — see
    // Dump{T}(T, int, string?) below for the one LINQPad shape that stays hinted.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static T Dump<T>(this T o,
        string? label = null,
        int? maxDepth = null,
        int? maxRows = null,
        int? collapseTo = null,
        bool noTotals = false,
        int? repeatHeadersAt = null,
        bool includePrivateMembers = false,
        bool alphabetizeMembers = false,
        bool forceExpansion = false,
        bool useInvariantCultureForNumbers = false,
        [CallerArgumentExpression(nameof(o))] string? expression = null)
    {
        var options = new DotPad.Runtime.DumpOptions
        {
            MaxDepth = maxDepth ?? 3,
            MaxRows = maxRows ?? 1000,
            CollapseTo = collapseTo,
            NoTotals = noTotals,
            RepeatHeadersAt = repeatHeadersAt,
            IncludePrivateMembers = includePrivateMembers,
            AlphabetizeMembers = alphabetizeMembers,
            ForceExpansion = forceExpansion,
            UseInvariantCultureForNumbers = useInvariantCultureForNumbers,
        };
        return DumpCore(o, label ?? expression, options);
    }

    /// <summary>
    /// LINQPad-shaped overload for <c>x.Dump(3)</c> — sets <paramref name="depth"/> positionally.
    /// Needed as its own overload (rather than folding into
    /// <see cref="Dump{T}(T, string?, int?, int?, int?, bool, int?, bool, bool, bool, bool, string?)"/>)
    /// because a plain <see langword="int"/> argument can't bind to that overload's leading
    /// <c>string? label</c> parameter — <c>x.Dump(3)</c> wouldn't compile otherwise.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="depth">How many levels of nested objects/collections to expand before truncating.</param>
    /// <param name="expression">
    /// Supplied automatically by the compiler as the source text of <paramref name="o"/>, used as
    /// the dump's label — do not pass this explicitly.
    /// </param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, int depth,
        [CallerArgumentExpression(nameof(o))] string? expression = null)
        => DumpCore(o, expression, new DotPad.Runtime.DumpOptions { MaxDepth = depth });

    // Shared by both overloads above: renders to HTML and writes it out as the sentinel-prefixed
    // line CSharpLanguageHost/EditorTabItemControl scan stdout for (see DumpLinePrefixChar).
    private static T DumpCore<T>(T o, string? label, DotPad.Runtime.DumpOptions options)
    {
        var html = DotPad.Runtime.DumpRenderer.Render(o, label, options);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        Console.WriteLine(DumpLinePrefixChar + encoded);
        return o;
    }
}

// CallerArgumentExpression is not in netstandard2.0 BCL — define it so the C# compiler can use it.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) =>
            ParameterName = parameterName;

        public string ParameterName { get; }
    }
}
