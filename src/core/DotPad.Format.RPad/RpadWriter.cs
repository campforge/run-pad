using System.Text;

namespace DotPad.Format.RPad;

public static class RpadWriter
{
    /// <summary>Serializes the document back to .rpad file content.</summary>
    public static string Write(RpadDocument doc)
    {
        var yaml = SerializeFm(doc.FrontMatter);
        var code = doc.RunCode.EndsWith('\n') ? doc.RunCode : doc.RunCode + "\n";
        return $"---\n{yaml}\n---\n{doc.BodyBeforeCode}{code}{doc.BodyAfterCode}";
    }

    /// <summary>Generates the content for a brand-new .rpad file.</summary>
    public static string NewFile(string title = "Untitled", string targetSdk = "net10.0", RpadKind kind = RpadKind.Statements)
    {
        var code = kind switch
        {
            RpadKind.Program => "void Main()\n{\n\n}\n\n// You can define other methods, fields, classes and namespaces here\n",
            RpadKind.Expression => "\"Hello, RPad!\"\n",
            _ => "Console.WriteLine(\"Hello, RPad!\");\n",
        };
        return $"---\nversion: 1\nlanguage: csharp\ntargetSdk: {targetSdk}\nkind: {kind}\n---\n\n# {title}\n\n```csharp run\n{code}```\n";
    }

    private static string SerializeFm(RpadFrontMatter fm)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"version: {fm.Version}");
        sb.AppendLine($"language: {fm.Language.ToString().ToLowerInvariant()}");
        sb.AppendLine($"targetSdk: {fm.TargetSdk}");
        sb.Append($"kind: {fm.Kind}");

        if (fm.Packages?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("packages:");
            foreach (var (k, v) in fm.Packages)
            {
                sb.AppendLine();
                sb.Append($"  {YamlEscape(k)}: {YamlEscape(v)}");
            }
        }

        if (fm.References?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("references:");
            foreach (var r in fm.References)
            {
                sb.AppendLine();
                sb.Append($"  - {r}");
            }
        }

        if (fm.Usings?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("usings:");
            foreach (var u in fm.Usings)
            {
                sb.AppendLine();
                sb.Append($"  - {u}");
            }
        }

        return sb.ToString();
    }

    // Wraps value in single quotes if it contains YAML-special characters.
    // Single quotes inside the value are escaped by doubling them ('').
    private static string YamlEscape(string value)
    {
        if (value.Length == 0) return "''";
        bool needsQuote = value[0] is ':' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`' or '{'
            || value.Contains(": ") || value.Contains(" #") || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote) return value;
        return "'" + value.Replace("'", "''") + "'";
    }
}
