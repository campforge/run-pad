using System.Xml.Linq;

namespace DotPad.Format.RPad;

/// <summary>
/// Converts a LINQPad <c>.linq</c> file to an <see cref="RpadDocument"/> (TODO.md #63) — a <c>.linq</c>
/// file is an XML <c>&lt;Query&gt;</c> header (<c>Kind</c> attribute, <c>&lt;NuGetReference&gt;</c>/
/// <c>&lt;Namespace&gt;</c>/<c>&lt;Reference&gt;</c> children) followed by plain C#/VB source — no XML
/// wrapping on the code itself, so the header is parsed as an isolated fragment rather than the whole
/// file (which usually isn't valid XML end to end once the code is anything nontrivial).
/// </summary>
public static class LinqConverter
{
    // LINQPad's own supported Kind values — SQL/ESQL/F# ones have no DotPad-supported equivalent
    // (only C#/VB, see RpadLanguage) and are rejected explicitly below rather than silently
    // mis-converted.
    private static readonly Dictionary<string, (RpadLanguage Language, RpadKind Kind)> KindMap = new()
    {
        ["Statements"] = (RpadLanguage.CSharp, RpadKind.Statements),
        ["Program"] = (RpadLanguage.CSharp, RpadKind.Program),
        ["Expression"] = (RpadLanguage.CSharp, RpadKind.Expression),
        ["VBStatements"] = (RpadLanguage.Vb, RpadKind.Statements),
        ["VBProgram"] = (RpadLanguage.Vb, RpadKind.Program),
        ["VBExpression"] = (RpadLanguage.Vb, RpadKind.Expression),
    };

    /// <summary>
    /// Converts <paramref name="linqContent"/> (the full text of a <c>.linq</c> file) to an
    /// <see cref="RpadDocument"/>, ready for <see cref="RpadWriter.Write"/>. Throws
    /// <see cref="FormatException"/> if the file has no recognizable <c>&lt;Query&gt;</c> header, or
    /// its <c>Kind</c> has no DotPad-supported equivalent (e.g. F#, SQL).
    /// </summary>
    /// <param name="linqContent">The full text of a <c>.linq</c> file.</param>
    /// <param name="title">Heading for the converted document — typically the source file's own name without extension.</param>
    public static RpadDocument Convert(string linqContent, string title = "Untitled")
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
                $"Unsupported .linq Query Kind '{kindValue ?? "(none)"}' — DotPad only supports: {supported}.");
        }

        var packages = new Dictionary<string, Package>();
        foreach (var nugetRef in query.Elements("NuGetReference"))
        {
            var id = nugetRef.Value.Trim();
            if (id.Length == 0) continue;
            var version = nugetRef.Attribute("Version")?.Value;
            packages[id] = version is null ? Package.Default : new Package(version);
        }

        var namespaces = query.Elements("Namespace")
            .Select(e => e.Value.Trim())
            .Where(ns => ns.Length > 0)
            .ToList();

        var references = query.Elements("Reference")
            .Select(e => (e.Attribute("Relative")?.Value ?? e.Value).Trim())
            .Where(r => r.Length > 0)
            .ToList();

        var frontMatter = new RpadFrontMatter
        {
            Language = mapped.Language,
            Kind = mapped.Kind,
            Packages = packages,
            References = references,
            Namespaces = namespaces,
        };

        // BodyBeforeCode/BodyAfterCode must carry the actual Markdown fence markers themselves
        // (see RpadDocument's own doc comments) — RpadWriter.Write concatenates them around RunCode
        // verbatim, it doesn't know how to fence a code block on its own (only RpadWriter.NewFile
        // does, for a from-scratch document). Mirrors NewFile's own literal layout exactly so a
        // converted file looks like any other freshly-created one, not a special case.
        var languageId = mapped.Language.ToLanguageId();
        var bodyBeforeCode = $"\n# {title}\n\n```{languageId} run\n";
        const string bodyAfterCode = "```\n";

        return new RpadDocument
        {
            FrontMatter = frontMatter,
            RunCode = code,
            BodyBeforeCode = bodyBeforeCode,
            BodyAfterCode = bodyAfterCode,
        };
    }

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
