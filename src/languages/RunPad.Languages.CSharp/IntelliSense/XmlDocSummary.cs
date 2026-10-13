using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RunPad.Languages.CSharp.IntelliSense;

// Extracts plain-text <summary>/<param> content from a symbol's XML doc comment,
// so completion/signature-help/quick-info tooltips can show it under the highlighted
// signature the way LINQPad and VS Code do. Roslyn only hands back the raw, indented XML — nothing
// here comes pre-parsed.
internal static class XmlDocSummary
{
    public static string? GetSummary(ISymbol symbol) =>
        GetElementText(symbol, "summary", element => element);

    public static string? GetParameterSummary(ISymbol symbol, string parameterName) =>
        GetElementText(symbol, "param",
            element => element.Attributes("name").Any(a => a.Value == parameterName) ? element : null);

    private static string? GetElementText(ISymbol symbol, string elementName, Func<XElement, XElement?> match)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var element = doc.Descendants(elementName).Select(match).FirstOrDefault(e => e != null);
            if (element is null) return null;

            var sb = new StringBuilder();
            foreach (var node in element.Nodes()) AppendNode(node, sb);
            var text = CollapseWhitespace(sb.ToString());
            return text.Length == 0 ? null : text;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static void AppendNode(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText text:
                sb.Append(text.Value);
                break;
            case XElement { Name.LocalName: "paramref" or "typeparamref" } element:
                sb.Append((string?)element.Attribute("name"));
                break;
            case XElement { Name.LocalName: "see" or "seealso" } element:
                var cref = (string?)element.Attribute("cref");
                var langword = (string?)element.Attribute("langword");
                sb.Append(cref is not null ? ShortenCref(cref) : langword ?? element.Value);
                break;
            case XElement element:
                foreach (var child in element.Nodes()) AppendNode(child, sb);
                break;
        }
    }

    // cref values look like "T:System.String" or "M:System.String.Format(System.String)" — strip the
    // one-letter member-kind prefix, the parameter list, and everything but the final name segment.
    private static string ShortenCref(string cref)
    {
        var value = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parenIndex = value.IndexOf('(');
        if (parenIndex >= 0) value = value[..parenIndex];
        var lastDot = value.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < value.Length ? value[(lastDot + 1)..] : value;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
