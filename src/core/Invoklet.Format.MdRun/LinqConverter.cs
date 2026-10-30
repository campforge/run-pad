using System.Xml.Linq;

namespace Invoklet.Format.MdRun;

/// <summary>
/// Converts a LINQPad <c>.linq</c> file to an <see cref="MdRunDocument"/> — a <c>.linq</c>
/// file is an XML <c>&lt;Query&gt;</c> header (<c>Kind</c> attribute, <c>&lt;NuGetReference&gt;</c>/
/// <c>&lt;Namespace&gt;</c>/<c>&lt;RemoveNamespace&gt;</c>/<c>&lt;Reference&gt;</c> children) followed by plain C#/VB source — no XML
/// wrapping on the code itself, so the header is parsed as an isolated fragment rather than the whole
/// file (which usually isn't valid XML end to end once the code is anything nontrivial).
/// </summary>
public static partial class LinqConverter
{
    // LINQPad's own supported Kind values — SQL/ESQL/F# ones have no Invoklet-supported equivalent
    // (only C#/VB, see MdRunLanguage) and are rejected explicitly below rather than silently
    // mis-converted.
    private static readonly Dictionary<string, (MdRunLanguage Language, MdRunKind Kind)> KindMap = new()
    {
        ["Statements"] = (MdRunLanguage.CSharp, MdRunKind.Statements),
        ["Program"] = (MdRunLanguage.CSharp, MdRunKind.Program),
        ["Expression"] = (MdRunLanguage.CSharp, MdRunKind.Expression),
        ["VBStatements"] = (MdRunLanguage.Vb, MdRunKind.Statements),
        ["VBProgram"] = (MdRunLanguage.Vb, MdRunKind.Program),
        ["VBExpression"] = (MdRunLanguage.Vb, MdRunKind.Expression),
    };

    // LINQPad omits its standard imports from query headers. Persist this compatibility profile
    // only in converted C# documents, so ordinary scripts retain their own import defaults.
    private static readonly string[] CSharpDefaultNamespaces =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Data",
        "System.Diagnostics", "System.IO", "System.Linq", "System.Linq.Expressions",
        "System.Reflection", "System.Text", "System.Text.RegularExpressions", "System.Threading",
        "System.Transactions", "System.Xml", "System.Xml.Linq", "System.Xml.XPath",
    ];

    /// <summary>
    /// Converts <paramref name="linqContent"/> (the full text of a <c>.linq</c> file) to an
    /// <see cref="MdRunDocument"/>, ready for <see cref="MdRunWriter.Write"/>. Throws
    /// <see cref="FormatException"/> if the file has no recognizable <c>&lt;Query&gt;</c> header, or
    /// its <c>Kind</c> has no Invoklet-supported equivalent (e.g. F#, SQL).
    /// </summary>
    /// <param name="linqContent">The full text of a <c>.linq</c> file.</param>
    /// <param name="title">Heading for the converted document — typically the source file's own name without extension.</param>
    public static MdRunDocument Convert(string linqContent, string title = "Untitled")
    {
        var (queryXml, code) = SplitQueryHeader(linqContent);

        XElement query;
        try
        {
            query = XElement.Parse(queryXml);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Not a valid .linq file: <Query> header is not well-formed XML: {ex.Message}", ex);
        }

        var kindValue = query.Attribute("Kind")?.Value;
        if (kindValue is null || !KindMap.TryGetValue(kindValue, out var mapped))
        {
            var supported = string.Join(", ", KindMap.Keys);
            throw new FormatException(
                $"Unsupported .linq Query Kind '{kindValue ?? "(none)"}' — Invoklet only supports: {supported}.");
        }

        var packages = new Dictionary<string, Package>();
        foreach (var nugetRef in query.Elements("NuGetReference"))
        {
            var id = nugetRef.Value.Trim();
            if (id.Length == 0) continue;
            var version = nugetRef.Attribute("Version")?.Value;
            packages[id] = version is null ? Package.Default : new Package(version);
        }

        var explicitNamespaces = query.Elements("Namespace")
            .Select(e => e.Value.Trim())
            .Where(ns => ns.Length > 0);
        // A query can drop some of LINQPad's standard imports with <RemoveNamespace> (to avoid an
        // ambiguity such as System.Threading.Timer vs System.Timers.Timer); keeping them would make
        // the converted script fail to compile.
        var removedNamespaces = query.Elements("RemoveNamespace")
            .Select(e => e.Value.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var namespaces = mapped.Language == MdRunLanguage.CSharp
            ? CSharpDefaultNamespaces.Where(ns => !removedNamespaces.Contains(ns))
                .Concat(explicitNamespaces).Distinct(StringComparer.Ordinal).ToList()
            : explicitNamespaces.ToList();

        var references = query.Elements("Reference")
            .Select(e => (e.Attribute("Relative")?.Value ?? e.Value).Trim())
            .Where(r => r.Length > 0)
            .ToList();

        var frontMatter = new MdRunFrontMatter
        {
            Language = mapped.Language,
            Kind = mapped.Kind,
            Packages = packages,
            References = references,
            Namespaces = namespaces,
        };

        // BodyBeforeCode/BodyAfterCode must carry the actual Markdown fence markers themselves
        // (see MdRunDocument's own doc comments) — MdRunWriter.Write concatenates them around RunCode
        // verbatim, it doesn't know how to fence a code block on its own (only MdRunWriter.NewFile
        // does, for a from-scratch document). Mirrors NewFile's own literal layout exactly so a
        // converted file looks like any other freshly-created one, not a special case.
        var languageId = mapped.Language.ToLanguageId();
        var bodyBeforeCode = $"\n# {title}\n\n```{languageId} run\n";
        const string bodyAfterCode = "```\n";

        return new MdRunDocument
        {
            FrontMatter = frontMatter,
            RunCode = WithInvokletSecrets(code),
            BodyBeforeCode = bodyBeforeCode,
            BodyAfterCode = bodyAfterCode,
        };
    }

    // LINQPad's password manager calls by name: Util.GetPassword(name) reads a saved password (asking
    // for it when there is none) and Util.SetPassword(name, value) saves one. In Invoklet that is the
    // Secret Store's Util.GetSecret/SetSecret, and Invoklet has no Util.GetPassword. The calls are
    // renamed as written, in C# and VB alike.
    internal static string WithInvokletSecrets(string code) =>
        LinqPadPasswordCall().Replace(code, m => "Util." + (m.Groups[1].Value == "Get" ? "GetSecret" : "SetSecret") + "(");

    [System.Text.RegularExpressions.GeneratedRegex(@"\bUtil\s*\.\s*(Get|Set)Password\s*\(")]
    private static partial System.Text.RegularExpressions.Regex LinqPadPasswordCall();

    // The <Query> header is the only part of a .linq file that's actually XML — everything after it
    // is plain source code, which usually isn't well-formed XML on its own (unescaped '<'/'&', etc.),
    // so the header must be isolated as its own text fragment before parsing rather than treating the
    // whole file as one XML document.
    private static (string QueryXml, string Code) SplitQueryHeader(string content)
    {
        const string startTag = "<Query";
        var startIdx = content.IndexOf(startTag, StringComparison.Ordinal);
        if (startIdx < 0)
            throw new FormatException("Not a valid .linq file: no <Query> header found.");

        const string endTag = "</Query>";
        var closeTagIdx = content.IndexOf(endTag, startIdx, StringComparison.Ordinal);
        int endIdx;
        if (closeTagIdx >= 0)
        {
            endIdx = closeTagIdx + endTag.Length;
        }
        else
        {
            var selfCloseIdx = content.IndexOf("/>", startIdx, StringComparison.Ordinal);
            if (selfCloseIdx < 0)
                throw new FormatException("Not a valid .linq file: <Query> header is never closed.");
            endIdx = selfCloseIdx + 2;
        }

        var queryXml = content[startIdx..endIdx];
        var code = content[endIdx..].TrimStart('\r', '\n');
        return (queryXml, code);
    }
}
