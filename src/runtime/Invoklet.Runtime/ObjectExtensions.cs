using System.Runtime.CompilerServices;
using System.Text;
using Invoklet.Runtime;

// ReSharper disable once CheckNamespace

/// <summary>
/// The <c>.Dump()</c> extension every script calls to render a value to the output pane. Kept
/// call-compatible with LINQPad's real <c>LINQPad.Extensions</c> (signatures verified against the
/// real <c>LINQPad.Runtime.dll</c>): <c>x.Dump()</c>, <c>x.Dump("label")</c>, <c>x.Dump(3)</c>,
/// <c>x.Dump("label", 3)</c>, <c>x.Dump(true)</c>, <c>x.Dump("label", true)</c> and
/// <c>x.Dump(new DumpOptions { ... })</c> all work unchanged, so scripts ported from LINQPad don't
/// need to touch their <c>.Dump()</c> calls. Deliberately NOT ported: LINQPad's two big
/// 10-/11-positional-parameter overloads (<c>Dump(description, depth, collapseTo, toDataGrid,
/// exclude, include, alpha, noTotals, repeatHeadersAt, includePrivate)</c> and its
/// argument-order sibling) — <see cref="Dump{T}(T, DumpOptions)"/> already covers every one of those
/// same options with named properties instead of eleven positional bools/strings/ints, and Invoklet has
/// its own regression test (CSharpIntelliSenseSignatureHelpTests) pinning "no Dump overload shows a
/// many-parameter wall in signature help" as a deliberate design choice. A script using that exact
/// positional shape is the one LINQPad call pattern this doesn't reproduce byte-for-byte.
/// </summary>
public static class ObjectExtensions
{
    internal const char DumpLinePrefixChar = '\x1e'; // ASCII Record Separator — non-printable, won't appear in normal output

    /// <summary>
    /// Renders <paramref name="o"/> to the output pane and returns it unchanged, so a <c>.Dump()</c>
    /// call can be inserted inline without breaking an expression chain. Labeled with
    /// <paramref name="expression"/> — the source text of <paramref name="o"/> itself (e.g.
    /// <c>myList.Dump()</c> is headed "myList") — since no <paramref name="label"/> was given. Covers
    /// both real LINQPad shapes <c>Dump()</c> and <c>Dump(description)</c> at once — the
    /// auto-labeling from the source expression is a Invoklet-specific bonus on top, invisible to a
    /// caller (compiler-supplied, not something LINQPad scripts can pass explicitly), so it doesn't
    /// change how either real overload resolves.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>; unconstrained, so this works on any value, including <see langword="null"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="label">Heading shown above the rendered value.</param>
    /// <param name="expression">
    /// Supplied automatically by the compiler as the source text of <paramref name="o"/> — do not
    /// pass this explicitly. Used as the heading only when <paramref name="label"/> is omitted.
    /// </param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o,
        string? label = null,
        [CallerArgumentExpression(nameof(o))] string? expression = null)
        => DumpCore(o, label ?? expression, new DumpOptions());

    /// <summary>
    /// LINQPad-compatible <c>x.Dump(3)</c>/<c>x.Dump(3, 1)</c> — expands nested objects/collections
    /// <paramref name="depth"/> levels deep instead of the default of 3, optionally rendering
    /// anything past <paramref name="collapseTo"/> levels collapsed (still expandable by clicking).
    /// Unlabeled: unlike <see cref="Dump{T}(T, string?, string?)"/>, a plain depth doesn't auto-label
    /// from the source expression — use <see cref="Dump{T}(T, string?, int)"/> if you want both.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="depth">How many levels of nested objects/collections to expand before truncating.</param>
    /// <param name="collapseTo">If set, nested objects deeper than this level render collapsed instead of always expanded.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, int depth, int? collapseTo = null)
        => DumpCore(o, null, new DumpOptions { MaxDepth = depth, CollapseTo = collapseTo });

    /// <summary>
    /// LINQPad-shaped overload for <c>x.Dump("label", 3)</c> — sets both the heading and the
    /// expansion depth explicitly. Invoklet-specific — real LINQPad has no (description, depth) shape
    /// of its own (only the unlabeled <see cref="Dump{T}(T, int, int?)"/> and the big positional
    /// overloads this class deliberately doesn't port — see the class doc comment), but nothing about
    /// this collides with or shadows any real overload, so it stays as a convenience.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="label">Heading shown above the rendered value.</param>
    /// <param name="depth">How many levels of nested objects/collections to expand before truncating.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, string? label, int depth)
        => DumpCore(o, label, new DumpOptions { MaxDepth = depth });

    /// <summary>LINQPad-compatible <c>x.Dump(true)</c>/<c>x.Dump(true, "label")</c>. <see cref="DumpOptionsCore.ToDataGrid"/> is accepted for signature compatibility — Invoklet currently renders every collection the same way regardless.</summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="toDataGrid">Accepted for LINQPad-signature compatibility; not currently distinguished from the default rendering.</param>
    /// <param name="description">Heading shown above the rendered value.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, bool toDataGrid, string? description = null)
        => DumpCore(o, description, new DumpOptions { ToDataGrid = toDataGrid });

    /// <summary>LINQPad-compatible <c>x.Dump("label", true)</c> — same as <see cref="Dump{T}(T, bool, string?)"/> with the arguments in the other order real LINQPad also exposes.</summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="description">Heading shown above the rendered value.</param>
    /// <param name="toDataGrid">Accepted for LINQPad-signature compatibility; not currently distinguished from the default rendering.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, string? description, bool toDataGrid)
        => DumpCore(o, description, new DumpOptions { ToDataGrid = toDataGrid });

    /// <summary>
    /// Renders <paramref name="o"/> with full control over every rendering option (row limits,
    /// collapsing, member visibility, ...) — see <see cref="DumpOptions"/>. For everyday use, prefer
    /// the plain overloads above instead.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="options">Rendering options, including the heading (<see cref="DumpOptions.Description"/>).</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, DumpOptions options)
        => DumpCore(o, options.Description, options);

    // Shared by every overload above: renders to HTML and writes it out as the sentinel-prefixed
    // line CSharpLanguageHost/EditorTabItemControl scan stdout for (see DumpLinePrefixChar).
    private static T DumpCore<T>(T o, string? label, DumpOptions options)
    {
        var html = DumpRenderer.Render(o, label, options);
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
