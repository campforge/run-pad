using System.Runtime.CompilerServices;
using System.Text;
using DotPad.Runtime;

// ReSharper disable once CheckNamespace

/// <summary>
/// The <c>.Dump()</c> extension every script calls to render a value to the output pane. Kept
/// call-compatible with LINQPad's original <c>ObjectExtensions</c>: <c>x.Dump()</c>,
/// <c>x.Dump("label")</c>, <c>x.Dump(3)</c> and <c>x.Dump("label", 3)</c> all work unchanged, so
/// scripts ported from LINQPad don't need to touch their <c>.Dump()</c> calls. DotPad-specific
/// rendering options (row limits, collapsing, member visibility, ...) live on the separate
/// <see cref="Dump{T}(T, DumpOptions)"/> overload instead of extra parameters here.
/// </summary>
public static class ObjectExtensions
{
    internal const char DumpLinePrefixChar = '\x1e'; // ASCII Record Separator — non-printable, won't appear in normal output

    /// <summary>
    /// Renders <paramref name="o"/> to the output pane and returns it unchanged, so a <c>.Dump()</c>
    /// call can be inserted inline without breaking an expression chain. Labeled with
    /// <paramref name="expression"/> — the source text of <paramref name="o"/> itself (e.g.
    /// <c>myList.Dump()</c> is headed "myList") — since no <paramref name="label"/> was given.
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
    /// LINQPad-shaped overload for <c>x.Dump(3)</c> — expands nested objects/collections
    /// <paramref name="depth"/> levels deep instead of the default of 3. Unlabeled: unlike
    /// <see cref="Dump{T}(T, string?, string?)"/>, a plain depth doesn't auto-label from the
    /// source expression — use <see cref="Dump{T}(T, string?, int)"/> if you want both.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="depth">How many levels of nested objects/collections to expand before truncating.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, int depth)
        => DumpCore(o, null, new DumpOptions { MaxDepth = depth });

    /// <summary>
    /// LINQPad-shaped overload for <c>x.Dump("label", 3)</c> — sets both the heading and the
    /// expansion depth explicitly.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="label">Heading shown above the rendered value.</param>
    /// <param name="depth">How many levels of nested objects/collections to expand before truncating.</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, string? label, int depth)
        => DumpCore(o, label, new DumpOptions { MaxDepth = depth });

    /// <summary>
    /// Renders <paramref name="o"/> with full control over every DotPad-specific rendering option
    /// (row limits, collapsing, member visibility, ...) — see <see cref="DumpOptions"/>. For
    /// everyday use, prefer the plain overloads above instead.
    /// </summary>
    /// <typeparam name="T">Inferred from <paramref name="o"/>.</typeparam>
    /// <param name="o">The value to render.</param>
    /// <param name="options">Rendering options, including the heading (<see cref="DumpOptions.Label"/>).</param>
    /// <returns><paramref name="o"/>, unchanged.</returns>
    public static T Dump<T>(this T o, DumpOptions options)
        => DumpCore(o, options.Label, options);

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
