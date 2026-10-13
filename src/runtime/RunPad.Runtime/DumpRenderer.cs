using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace RunPad.Runtime;

internal static class DumpRenderer
{
    private const int MaxStringLength = 500;
    private const int DefaultMaxDepth = 3;
    private const int DefaultMaxRows = 1000;

    // Kept for existing callers/tests that only care about the original two options.
    public static string Render(object? value, string? label, int? maxDepth = null, int? maxRows = null)
        => Render(value, label, new DumpOptions { MaxDepth = maxDepth, MaxRows = maxRows });

    public static string Render(object? value, string? label, DumpOptions options)
    {
        // Every .Dump() call starts from the process-wide defaults (LINQPad-compatible
        // Util.DumpDefaults/DumpOptions.Default) with this specific call's own options layered on top
        // — see DumpOptions.CombineWith.
        var effective = DumpOptions.GlobalDefaults.CombineWith(options);

        var sb = new StringBuilder();
        if (label != null)
            sb.Append("<div class=\"dp-label\">").Append(Encode(label)).Append("</div>");
        RenderValue(sb, value, 0, effective);
        return sb.ToString();
    }

    private static IFormatProvider NumberFormat(DumpOptions options) =>
        options.UseInvariantCultureForNumbers == true ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;

    // ForceExpansion always wins; otherwise collapse once depth passes CollapseTo (if set).
    private static bool ShouldCollapse(DumpOptions options, int depth) =>
        !options.ForceExpansion && options.CollapseTo is { } n && depth >= n;

    // ── Value dispatch ────────────────────────────────────────────────────────

    private static void RenderValue(StringBuilder sb, object? value, int depth, DumpOptions options)
    {
        if (value is null)
        {
            sb.Append("<span class=\"dp-null\">null</span>");
            return;
        }

        var type = value.GetType();

        if (IsPrimitiveLike(type))
        {
            RenderPrimitive(sb, value, type, options);
            return;
        }

        // Not depth-limited like an ordinary nested property would be — a DumpContainer exists
        // specifically to keep showing LIVE content for as long as the script runs, so truncating it
        // just because it happened to be nested deep would be inconsistent with every later
        // Content/UpdateContent update (RenderForFirstDump's caller here) never re-checking depth at all.
        if (value is DumpContainer container)
        {
            sb.Append(container.RenderForFirstDump(options));
            return;
        }

        if (depth >= (options.MaxDepth ?? DefaultMaxDepth))
        {
            sb.Append("<span class=\"dp-truncated\">").Append(Encode(SafeToString(value))).Append("</span>");
            return;
        }

        if (value is ICustomMemberProvider custom)
        {
            RenderCustomMemberProvider(sb, custom, depth, options);
            return;
        }

        if (value is IDictionary dict)
        {
            RenderDictionary(sb, dict, depth, options);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            RenderEnumerable(sb, enumerable, type, depth, options);
            return;
        }

        RenderObject(sb, value, type, depth, options);
    }

    // void, not string — every call site invokes this as a statement, and materializing outer's
    // entire accumulated buffer via ToString() on every nesting level (as the old signature required)
    // meant O(n^2) allocations for a deep/wide dump.
    private static void RenderCollapsible(StringBuilder outer, int depth, DumpOptions options, Action<StringBuilder> renderInner)
    {
        var inner = new StringBuilder();
        renderInner(inner);
        if (ShouldCollapse(options, depth))
        {
            outer.Append("<details class=\"dp-collapse\"><summary>(click to expand)</summary>")
                 .Append(inner).Append("</details>");
        }
        else
        {
            outer.Append(inner);
        }
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    // DateOnly/TimeOnly (.NET 6+) can't be referenced by type here — this library targets
    // netstandard2.0 (predates both), which is fine at runtime (netstandard2.0 is ABI-compatible with
    // the net10.0 process a compiled script actually runs as) but means detecting/formatting them can
    // only go through reflection (FullName match + the IFormattable interface, which IS available in
    // netstandard2.0), never a direct `is DateOnly`/`(DateOnly)value` in this project's own source.
    private static bool IsDateOnlyOrTimeOnly(Type type) => type.FullName is "System.DateOnly" or "System.TimeOnly";

    private static readonly HashSet<Type> IntegralNumberTypes =
    [
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
    ];

    private static readonly HashSet<Type> RealNumberTypes = [typeof(float), typeof(double), typeof(decimal)];

    private static bool IsPrimitiveLike(Type type) =>
        type.IsPrimitive ||
        type == typeof(string) ||
        type == typeof(decimal) ||
        type == typeof(char) ||
        type == typeof(bool) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) ||
        type == typeof(Guid) ||
        type == typeof(Uri) ||
        type.IsEnum ||
        IsDateOnlyOrTimeOnly(type);

    private static void RenderPrimitive(StringBuilder sb, object value, Type type, DumpOptions options)
    {
        if (IsDateOnlyOrTimeOnly(type))
        {
            var format = type.FullName == "System.DateOnly" ? options.FormatStrings.DateOnly : options.FormatStrings.TimeOnly;
            var text = ((IFormattable)value).ToString(format ?? "o", NumberFormat(options));
            sb.Append("<span class=\"dp-datetime\">").Append(Encode(text)).Append("</span>");
            return;
        }

        switch (value)
        {
            case string s:
                {
                    var v = s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) + "…" : s;
                    sb.Append("<span class=\"dp-string\">&quot;").Append(Encode(v)).Append("&quot;</span>");
                    break;
                }
            case char c:
                sb.Append("<span class=\"dp-string\">&apos;").Append(Encode(c.ToString())).Append("&apos;</span>");
                break;
            case bool b:
                sb.Append(b ? "<span class=\"dp-bool\">true</span>" : "<span class=\"dp-bool\">false</span>");
                break;
            case DateTime dt:
                sb.Append("<span class=\"dp-datetime\">").Append(Encode(dt.ToString(options.FormatStrings.DateTime ?? "o"))).Append("</span>");
                break;
            case DateTimeOffset dto:
                sb.Append("<span class=\"dp-datetime\">").Append(Encode(dto.ToString(options.FormatStrings.DateTimeOffset ?? "o"))).Append("</span>");
                break;
            case TimeSpan ts:
                sb.Append("<span class=\"dp-datetime\">").Append(Encode(ts.ToString())).Append("</span>");
                break;
            case Guid g:
                sb.Append("<span class=\"dp-string\">").Append(Encode(g.ToString())).Append("</span>");
                break;
            case Uri uri:
                sb.Append("<span class=\"dp-string\">").Append(Encode(uri.ToString())).Append("</span>");
                break;
            default:
                var css = type.IsEnum ? "dp-enum" : "dp-number";
                var format = IntegralNumberTypes.Contains(type) ? options.FormatStrings.IntegralNumber
                    : RealNumberTypes.Contains(type) ? options.FormatStrings.RealNumber
                    : null;
                var text = type.IsEnum
                    ? value.ToString() ?? ""
                    : format is not null
                        ? ((IFormattable)value).ToString(format, NumberFormat(options))
                        : Convert.ToString(value, NumberFormat(options)) ?? value.ToString() ?? "";
                sb.Append("<span class=\"").Append(css).Append("\">")
                  .Append(Encode(text)).Append("</span>");
                break;
        }
    }

    // ── Custom member provider ───────────────────────

    private static void RenderCustomMemberProvider(StringBuilder sb, ICustomMemberProvider provider, int depth, DumpOptions options)
    {
        var names = provider.GetNames().ToArray();
        var types = provider.GetTypes().ToArray();
        var values = provider.GetValues().ToArray();
        var count = Math.Min(names.Length, values.Length);

        RenderCollapsible(sb, depth, options, inner =>
        {
            inner.Append("<table class=\"dp-table\"><thead><tr>")
                 .Append("<th class=\"dp-th\">Name</th><th class=\"dp-th\">Value</th>")
                 .Append("</tr></thead><tbody>");
            for (var i = 0; i < count; i++)
            {
                inner.Append("<tr><td class=\"dp-td dp-propname\">").Append(Encode(names[i])).Append("</td><td class=\"dp-td\">");
                // GetTypes() (LINQPad-compatible — verified against the real LINQPad.Runtime.dll,
                // which has 3 methods on this interface, not the 2 RunPad originally shipped with)
                // gives a null value's DECLARED type, which plain reflection over the boxed null
                // obviously can't — rendering "null (Foo)" instead of a bare "null" for it.
                if (values[i] is null && i < types.Length && types[i] is { } declaredType)
                    inner.Append("<span class=\"dp-null\">null (").Append(Encode(FriendlyName(declaredType))).Append(")</span>");
                else
                    RenderValue(inner, values[i], depth + 1, options);
                inner.Append("</td></tr>");
            }
            inner.Append("</tbody></table>");
        });
    }

    // ── Dictionary ────────────────────────────────────────────────────────────

    private static void RenderDictionary(StringBuilder sb, IDictionary dict, int depth, DumpOptions options)
    {
        var count = dict.Count;
        if (!options.NoTotals)
            sb.Append("<div class=\"dp-count\">").Append(count).Append(' ').Append(Plural(count, "item")).Append("</div>");

        RenderCollapsible(sb, depth, options, inner =>
        {
            inner.Append("<table class=\"dp-table\"><thead><tr>")
                 .Append("<th class=\"dp-th\">Key</th><th class=\"dp-th\">Value</th>")
                 .Append("</tr></thead><tbody>");
            int row = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (row >= (options.MaxRows ?? DefaultMaxRows))
                {
                    inner.Append("<tr><td class=\"dp-td dp-truncated\" colspan=\"2\">… ").Append(count - (options.MaxRows ?? DefaultMaxRows)).Append(" more</td></tr>");
                    break;
                }
                MaybeRepeatHeader(inner, row, options, () =>
                    inner.Append("<tr><th class=\"dp-th\">Key</th><th class=\"dp-th\">Value</th></tr>"));
                inner.Append("<tr><td class=\"dp-td\">");
                RenderValue(inner, entry.Key, depth + 1, options);
                inner.Append("</td><td class=\"dp-td\">");
                RenderValue(inner, entry.Value, depth + 1, options);
                inner.Append("</td></tr>");
                row++;
            }
            inner.Append("</tbody></table>");
        });
    }

    // ── Enumerable ────────────────────────────────────────────────────────────

    private static void RenderEnumerable(StringBuilder sb, IEnumerable enumerable, Type collectionType, int depth, DumpOptions options)
    {
        var maxRows = options.MaxRows ?? DefaultMaxRows;
        var items = new List<object?>();
        foreach (var item in enumerable)
        {
            items.Add(item);
            if (items.Count > maxRows) break;
        }

        var shownCount = Math.Min(items.Count, maxRows);
        var hasMore = items.Count > maxRows;

        if (!options.NoTotals)
        {
            sb.Append("<div class=\"dp-count\">")
              .Append(Encode(FriendlyName(collectionType)))
              .Append(" &mdash; ").Append(shownCount)
              .Append(hasMore ? "+" : "")
              .Append(' ').Append(Plural(shownCount, "item")).Append("</div>");
        }

        if (shownCount == 0) { sb.Append("<span class=\"dp-truncated\">empty</span>"); return; }

        var firstNonNull = items.Take(shownCount).FirstOrDefault(x => x != null);
        bool isPrimitive = firstNonNull == null || IsPrimitiveLike(firstNonNull.GetType());

        RenderCollapsible(sb, depth, options, inner =>
        {
            if (isPrimitive)
            {
                inner.Append("<table class=\"dp-table\"><thead><tr>")
                     .Append("<th class=\"dp-th\" style=\"width:40px\">#</th><th class=\"dp-th\">Value</th>")
                     .Append("</tr></thead><tbody>");
                for (int i = 0; i < shownCount; i++)
                {
                    MaybeRepeatHeader(inner, i, options, () =>
                        inner.Append("<tr><th class=\"dp-th\">#</th><th class=\"dp-th\">Value</th></tr>"));
                    inner.Append("<tr><td class=\"dp-td dp-number\">").Append(i).Append("</td><td class=\"dp-td\">");
                    RenderValue(inner, items[i], depth + 1, options);
                    inner.Append("</td></tr>");
                }
            }
            else
            {
                var props = GetMembers(firstNonNull!.GetType(), options);

                if (props.Length == 0)
                {
                    inner.Append("<table class=\"dp-table\"><thead><tr><th class=\"dp-th\">Value</th></tr></thead><tbody>");
                    for (int i = 0; i < shownCount; i++)
                    {
                        inner.Append("<tr><td class=\"dp-td\">");
                        RenderValue(inner, items[i], depth + 1, options);
                        inner.Append("</td></tr>");
                    }
                }
                else
                {
                    inner.Append("<table class=\"dp-table\"><thead><tr>");
                    foreach (var p in props)
                        inner.Append("<th class=\"dp-th\">").Append(Encode(p.Name)).Append("</th>");
                    inner.Append("</tr></thead><tbody>");
                    for (int i = 0; i < shownCount; i++)
                    {
                        MaybeRepeatHeader(inner, i, options, () =>
                        {
                            inner.Append("<tr>");
                            foreach (var p in props) inner.Append("<th class=\"dp-th\">").Append(Encode(p.Name)).Append("</th>");
                            inner.Append("</tr>");
                        });

                        var item = items[i];
                        inner.Append("<tr>");
                        if (item is null)
                        {
                            inner.Append("<td class=\"dp-td dp-null\" colspan=\"").Append(props.Length).Append("\">null</td>");
                        }
                        else
                        {
                            foreach (var p in props)
                            {
                                inner.Append("<td class=\"dp-td\">");
                                try { RenderValue(inner, GetMemberValue(p, item), depth + 1, options); }
                                catch (Exception ex) { inner.Append("<span class=\"dp-error\">").Append(Encode(ex.Message)).Append("</span>"); }
                                inner.Append("</td>");
                            }
                        }
                        inner.Append("</tr>");
                    }
                }
            }

            if (hasMore)
                // colspan="99" is a deliberately oversized value, not a real column count — browsers
                // clamp it to however many columns the row's table actually has, so this spans the
                // full width regardless of whether the row came from the primitive-value branch (2
                // columns) or the property-grid branch (however many properties there are).
                inner.Append("<tr><td class=\"dp-td dp-truncated\" colspan=\"99\">… more items</td></tr>");
            inner.Append("</tbody></table>");
        });
    }

    // ── Object (property grid) ────────────────────────────────────────────────

    private static void RenderObject(StringBuilder sb, object value, Type type, int depth, DumpOptions options)
    {
        var members = GetMembers(type, options);

        if (members.Length == 0)
        {
            sb.Append("<span class=\"dp-truncated\">").Append(Encode(SafeToString(value))).Append("</span>");
            return;
        }

        sb.Append("<div class=\"dp-type\">").Append(Encode(FriendlyName(type))).Append("</div>");
        RenderCollapsible(sb, depth, options, inner =>
        {
            inner.Append("<table class=\"dp-table\"><thead><tr>")
                 .Append("<th class=\"dp-th\">Property</th><th class=\"dp-th\">Value</th>")
                 .Append("</tr></thead><tbody>");
            foreach (var p in members)
            {
                inner.Append("<tr><td class=\"dp-td dp-propname\">").Append(Encode(p.Name)).Append("</td><td class=\"dp-td\">");
                try { RenderValue(inner, GetMemberValue(p, value), depth + 1, options); }
                catch (Exception ex) { inner.Append("<span class=\"dp-error\">").Append(Encode(ex.Message)).Append("</span>"); }
                inner.Append("</td></tr>");
            }
            inner.Append("</tbody></table>");
        });
    }

    // ── Member reflection (properties + optionally fields, public + optionally non-public) ────

    private static MemberInfo[] GetMembers(Type type, DumpOptions options)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance
            | (options.IncludePrivateMembers ? BindingFlags.NonPublic : 0);

        IEnumerable<MemberInfo> members = type
            .GetProperties(flags)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>();

        if (options.IncludePrivateMembers)
            members = members.Concat(type.GetFields(flags)
                .Where(f => f.GetCustomAttribute<CompilerGeneratedAttribute>() is null));

        // LINQPad-compatible Dump(include:)/Dump(exclude:) — MembersToInclude/MembersToExclude are
        // comma-delimited strings (verified against the real LINQPad.Runtime.dll; RunPad originally
        // shipped these as string collections instead). Both apply, in this order (Include narrows
        // down first, Exclude then removes from what's left), rather than one overriding the other
        // outright — simpler to reason about if a caller ever sets both.
        if (SplitMembers(options.MembersToInclude) is { Length: > 0 } include)
            members = members.Where(m => include.Contains(m.Name, StringComparer.Ordinal));
        if (SplitMembers(options.MembersToExclude) is { Length: > 0 } exclude)
            members = members.Where(m => !exclude.Contains(m.Name, StringComparer.Ordinal));

        return options.AlphabetizeMembers == true
            ? members.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray()
            : members.ToArray();
    }

    // StringSplitOptions.TrimEntries isn't in netstandard2.0 (added in .NET 5) — trimmed by hand instead.
    private static string[]? SplitMembers(string? commaDelimited) => commaDelimited
        ?.Split(',')
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToArray();

    private static object? GetMemberValue(MemberInfo member, object target) => member switch
    {
        PropertyInfo p => p.GetValue(target),
        FieldInfo f => f.GetValue(target),
        _ => null,
    };

    private static void MaybeRepeatHeader(StringBuilder sb, int row, DumpOptions options, Action appendHeaderRow)
    {
        if (options.RepeatHeadersAt is { } every && every > 0 && row > 0 && row % every == 0)
            appendHeaderRow();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var idx = type.Name.IndexOf('`');
        var baseName = idx >= 0 ? type.Name.Substring(0, idx) : type.Name;
        var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return baseName + "<" + args + ">";
    }

    private static string Plural(int n, string noun) => n == 1 ? noun : noun + "s";

    private static string SafeToString(object? v)
    {
        try { return v?.ToString() ?? "null"; }
        catch { return "<" + (v?.GetType().Name ?? "?") + ">"; }
    }

    // internal, not private: Util.ProgressBar needs the same HTML-escaping for its
    // caption text — it renders its own fixed markup directly rather than going through Render(),
    // since a plain string handed to Render() escapes AND quotes it as a dumped string value.
    internal static string Encode(string? s)
    {
        if (s is null) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
    }
}
