using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotPad.Runtime;

// Bundles the LINQPad-style Dump() options (ANALYSIS.md feature #23) so RenderValue/RenderObject/
// RenderEnumerable don't have to grow a new positional parameter for each one.
internal sealed class DumpOptions
{
    public int MaxDepth = 3;
    public int MaxRows = 1000;
    public int? CollapseTo;
    public bool NoTotals;
    public int? RepeatHeadersAt;
    public bool IncludePrivateMembers;
    public bool AlphabetizeMembers;
    public bool ForceExpansion;
    public bool UseInvariantCultureForNumbers;

    public IFormatProvider NumberFormat => UseInvariantCultureForNumbers
        ? CultureInfo.InvariantCulture
        : CultureInfo.CurrentCulture;

    // ForceExpansion always wins; otherwise collapse once depth passes CollapseTo (if set).
    public bool ShouldCollapse(int depth) => !ForceExpansion && CollapseTo is { } n && depth >= n;
}

internal static class DumpRenderer
{
    private const int MaxStringLength = 500;

    // Kept for existing callers/tests that only care about the original two options.
    public static string Render(object? value, string? label, int? maxDepth = null, int? maxRows = null)
        => Render(value, label, new DumpOptions { MaxDepth = maxDepth ?? 3, MaxRows = maxRows ?? 1000 });

    public static string Render(object? value, string? label, DumpOptions options)
    {
        var sb = new StringBuilder();
        if (label != null)
            sb.Append("<div class=\"dp-label\">").Append(Encode(label)).Append("</div>");
        RenderValue(sb, value, 0, options);
        return sb.ToString();
    }

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

        if (depth >= options.MaxDepth)
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

    private static string RenderCollapsible(StringBuilder outer, int depth, DumpOptions options, Action<StringBuilder> renderInner)
    {
        var inner = new StringBuilder();
        renderInner(inner);
        if (options.ShouldCollapse(depth))
        {
            outer.Append("<details class=\"dp-collapse\"><summary>(click to expand)</summary>")
                 .Append(inner).Append("</details>");
        }
        else
        {
            outer.Append(inner);
        }
        return outer.ToString();
    }

    // ── Primitives ────────────────────────────────────────────────────────────

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
        type.IsEnum;

    private static void RenderPrimitive(StringBuilder sb, object value, Type type, DumpOptions options)
    {
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
                sb.Append("<span class=\"dp-datetime\">").Append(Encode(dt.ToString("o"))).Append("</span>");
                break;
            case DateTimeOffset dto:
                sb.Append("<span class=\"dp-datetime\">").Append(Encode(dto.ToString("o"))).Append("</span>");
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
                var text = type.IsEnum
                    ? value.ToString() ?? ""
                    : Convert.ToString(value, options.NumberFormat) ?? value.ToString() ?? "";
                sb.Append("<span class=\"").Append(css).Append("\">")
                  .Append(Encode(text)).Append("</span>");
                break;
        }
    }

    // ── Custom member provider (ANALYSIS.md feature #20) ───────────────────────

    private static void RenderCustomMemberProvider(StringBuilder sb, ICustomMemberProvider provider, int depth, DumpOptions options)
    {
        var names = provider.GetNames().ToArray();
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
                if (row >= options.MaxRows)
                {
                    inner.Append("<tr><td class=\"dp-td dp-truncated\" colspan=\"2\">… ").Append(count - options.MaxRows).Append(" more</td></tr>");
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
        var items = new List<object?>();
        foreach (var item in enumerable)
        {
            items.Add(item);
            if (items.Count > options.MaxRows) break;
        }

        var shownCount = Math.Min(items.Count, options.MaxRows);
        var hasMore = items.Count > options.MaxRows;

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

        return options.AlphabetizeMembers
            ? members.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray()
            : members.ToArray();
    }

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

    private static string Encode(string? s)
    {
        if (s is null) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
    }
}
