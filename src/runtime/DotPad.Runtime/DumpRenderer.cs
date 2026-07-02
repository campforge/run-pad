using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DotPad.Runtime;

internal static class DumpRenderer
{
    private const int MaxRows         = 1000;
    private const int MaxStringLength = 500;
    private const int DefaultMaxDepth = 3;

    public static string Render(object? value, string? label, int? maxDepth = null)
    {
        var sb = new StringBuilder();
        if (label != null)
            sb.Append("<div class=\"dp-label\">").Append(Encode(label)).Append("</div>");
        RenderValue(sb, value, 0, maxDepth ?? DefaultMaxDepth);
        return sb.ToString();
    }

    // ── Value dispatch ────────────────────────────────────────────────────────

    private static void RenderValue(StringBuilder sb, object? value, int depth, int maxDepth)
    {
        if (value is null)
        {
            sb.Append("<span class=\"dp-null\">null</span>");
            return;
        }

        var type = value.GetType();

        if (IsPrimitiveLike(type))
        {
            RenderPrimitive(sb, value, type);
            return;
        }

        if (depth >= maxDepth)
        {
            sb.Append("<span class=\"dp-truncated\">").Append(Encode(SafeToString(value))).Append("</span>");
            return;
        }

        if (value is IDictionary dict)
        {
            RenderDictionary(sb, dict, depth, maxDepth);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            RenderEnumerable(sb, enumerable, type, depth, maxDepth);
            return;
        }

        RenderObject(sb, value, type, depth, maxDepth);
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    private static bool IsPrimitiveLike(Type type) =>
        type.IsPrimitive           ||
        type == typeof(string)     ||
        type == typeof(decimal)    ||
        type == typeof(char)       ||
        type == typeof(bool)       ||
        type == typeof(DateTime)   ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan)   ||
        type == typeof(Guid)       ||
        type == typeof(Uri)        ||
        type.IsEnum;

    private static void RenderPrimitive(StringBuilder sb, object value, Type type)
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
                sb.Append("<span class=\"").Append(css).Append("\">")
                  .Append(Encode(value.ToString() ?? "")).Append("</span>");
                break;
        }
    }

    // ── Dictionary ────────────────────────────────────────────────────────────

    private static void RenderDictionary(StringBuilder sb, IDictionary dict, int depth, int maxDepth)
    {
        var count = dict.Count;
        sb.Append("<div class=\"dp-count\">").Append(count).Append(' ').Append(Plural(count, "item")).Append("</div>");
        sb.Append("<table class=\"dp-table\"><thead><tr>")
          .Append("<th class=\"dp-th\">Key</th><th class=\"dp-th\">Value</th>")
          .Append("</tr></thead><tbody>");
        int row = 0;
        foreach (DictionaryEntry entry in dict)
        {
            if (row >= MaxRows)
            {
                sb.Append("<tr><td class=\"dp-td dp-truncated\" colspan=\"2\">… ").Append(count - MaxRows).Append(" more</td></tr>");
                break;
            }
            sb.Append("<tr><td class=\"dp-td\">");
            RenderValue(sb, entry.Key, depth + 1, maxDepth);
            sb.Append("</td><td class=\"dp-td\">");
            RenderValue(sb, entry.Value, depth + 1, maxDepth);
            sb.Append("</td></tr>");
            row++;
        }
        sb.Append("</tbody></table>");
    }

    // ── Enumerable ────────────────────────────────────────────────────────────

    private static void RenderEnumerable(StringBuilder sb, IEnumerable enumerable, Type collectionType, int depth, int maxDepth)
    {
        var items = new List<object?>();
        foreach (var item in enumerable)
        {
            items.Add(item);
            if (items.Count > MaxRows) break;
        }

        var shownCount = Math.Min(items.Count, MaxRows);
        var hasMore    = items.Count > MaxRows;

        sb.Append("<div class=\"dp-count\">")
          .Append(Encode(FriendlyName(collectionType)))
          .Append(" &mdash; ").Append(shownCount)
          .Append(hasMore ? "+" : "")
          .Append(' ').Append(Plural(shownCount, "item")).Append("</div>");

        if (shownCount == 0) { sb.Append("<span class=\"dp-truncated\">empty</span>"); return; }

        var firstNonNull = items.Take(shownCount).FirstOrDefault(x => x != null);
        bool isPrimitive = firstNonNull == null || IsPrimitiveLike(firstNonNull.GetType());

        if (isPrimitive)
        {
            sb.Append("<table class=\"dp-table\"><thead><tr>")
              .Append("<th class=\"dp-th\" style=\"width:40px\">#</th><th class=\"dp-th\">Value</th>")
              .Append("</tr></thead><tbody>");
            for (int i = 0; i < shownCount; i++)
            {
                sb.Append("<tr><td class=\"dp-td dp-number\">").Append(i).Append("</td><td class=\"dp-td\">");
                RenderValue(sb, items[i], depth + 1, maxDepth);
                sb.Append("</td></tr>");
            }
        }
        else
        {
            var props = firstNonNull!.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray();

            if (props.Length == 0)
            {
                sb.Append("<table class=\"dp-table\"><thead><tr><th class=\"dp-th\">Value</th></tr></thead><tbody>");
                for (int i = 0; i < shownCount; i++)
                {
                    sb.Append("<tr><td class=\"dp-td\">");
                    RenderValue(sb, items[i], depth + 1, maxDepth);
                    sb.Append("</td></tr>");
                }
            }
            else
            {
                sb.Append("<table class=\"dp-table\"><thead><tr>");
                foreach (var p in props)
                    sb.Append("<th class=\"dp-th\">").Append(Encode(p.Name)).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                for (int i = 0; i < shownCount; i++)
                {
                    var item = items[i];
                    sb.Append("<tr>");
                    if (item is null)
                    {
                        sb.Append("<td class=\"dp-td dp-null\" colspan=\"").Append(props.Length).Append("\">null</td>");
                    }
                    else
                    {
                        foreach (var p in props)
                        {
                            sb.Append("<td class=\"dp-td\">");
                            try   { RenderValue(sb, p.GetValue(item), depth + 1, maxDepth); }
                            catch (Exception ex) { sb.Append("<span class=\"dp-error\">").Append(Encode(ex.Message)).Append("</span>"); }
                            sb.Append("</td>");
                        }
                    }
                    sb.Append("</tr>");
                }
            }
        }

        if (hasMore)
            sb.Append("<tr><td class=\"dp-td dp-truncated\" colspan=\"99\">… more items</td></tr>");
        sb.Append("</tbody></table>");
    }

    // ── Object (property grid) ────────────────────────────────────────────────

    private static void RenderObject(StringBuilder sb, object value, Type type, int depth, int maxDepth)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        if (props.Length == 0)
        {
            sb.Append("<span class=\"dp-truncated\">").Append(Encode(SafeToString(value))).Append("</span>");
            return;
        }

        sb.Append("<div class=\"dp-type\">").Append(Encode(FriendlyName(type))).Append("</div>");
        sb.Append("<table class=\"dp-table\"><thead><tr>")
          .Append("<th class=\"dp-th\">Property</th><th class=\"dp-th\">Value</th>")
          .Append("</tr></thead><tbody>");
        foreach (var p in props)
        {
            sb.Append("<tr><td class=\"dp-td dp-propname\">").Append(Encode(p.Name)).Append("</td><td class=\"dp-td\">");
            try   { RenderValue(sb, p.GetValue(value), depth + 1, maxDepth); }
            catch (Exception ex) { sb.Append("<span class=\"dp-error\">").Append(Encode(ex.Message)).Append("</span>"); }
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var idx      = type.Name.IndexOf('`');
        var baseName = idx >= 0 ? type.Name.Substring(0, idx) : type.Name;
        var args     = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return baseName + "<" + args + ">";
    }

    private static string Plural(int n, string noun) => n == 1 ? noun : noun + "s";

    private static string SafeToString(object? v)
    {
        try   { return v?.ToString() ?? "null"; }
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
