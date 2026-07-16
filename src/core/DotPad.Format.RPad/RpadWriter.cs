using System.Text;

namespace DotPad.Format.RPad;

public static class RpadWriter
{
    /// ToDo: надо разобраться со стримом, причем иметь возможность взять непосредственно стрим кода игнорируя заголовки
    /// <summary>Serializes the document back to .rpad file content.</summary>
    public static string Write(RpadDocument doc)
    {
        var yaml = SerializeFm(doc.FrontMatter);
        var code = doc.RunCode.EndsWith('\n') ? doc.RunCode : doc.RunCode + "\n";
        return $"---\n{yaml}\n---\n{doc.BodyBeforeCode}{code}{doc.BodyAfterCode}";
    }

    /// <summary>Generates the content for a brand-new .rpad file.</summary>
    // TODO.md #49 item 2 — language param added so "new file" can produce a .vb script too; the
    // enum member's own lowercased name is both the YAML `language:` value and the code-fence tag
    // (see RpadLanguage.cs), so it doubles as languageId here.
    public static string NewFile(
        string title = "Untitled",
        string targetSdk = "net10.0",
        RpadKind kind = RpadKind.Statements,
        RpadLanguage language = RpadLanguage.CSharp)
    {
        var languageId = language.ToLanguageId();

        var code = (language, kind) switch
        {
            (RpadLanguage.Vb, RpadKind.Program) =>
                "Module Program\nSub Main()\n\nEnd Sub\nEnd Module\n\n' You can define other methods, fields, classes and namespaces here\n",
            (RpadLanguage.Vb, RpadKind.Expression) => "\"Hello, RPad!\"\n",
            (RpadLanguage.Vb, _) => "Console.WriteLine(\"Hello, RPad!\")\n",
            (_, RpadKind.Program) => "void Main()\n{\n\n}\n\n// You can define other methods, fields, classes and namespaces here\n",
            (_, RpadKind.Expression) => "\"Hello, RPad!\"\n",
            _ => "Console.WriteLine(\"Hello, RPad!\");\n",
        };
        return $"---\nversion: \"1.0\"\nlanguage: {languageId}\ntargetSdk: {targetSdk}\nkind: {kind}\n---\n\n# {title}\n\n```{languageId} run\n{code}```\n";
    }

    private static string SerializeFm(RpadFrontMatter fm)
    {
        // Explicit '\n' throughout, not StringBuilder.AppendLine (which appends the *running
        // machine's* Environment.NewLine) — otherwise the very same document produced identical
        // byte-for-byte on Linux/macOS came out with CRLF front matter on Windows, needlessly
        // diverging from Write()'s own literal "\n" fences around it (ANALYSIS.md #93).
        var sb = new StringBuilder();
        sb.Append("version: ").Append(fm.Version).Append('\n');
        sb.Append("language: ").Append(fm.Language.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("targetSdk: ").Append(fm.TargetSdk).Append('\n');
        sb.Append("kind: ").Append(fm.Kind);

        if (fm.Packages?.Count > 0)
        {
            sb.Append('\n').Append("packages:");
            foreach (var (k, v) in fm.Packages)
                sb.Append('\n').Append("  ").Append(YamlEscape(k)).Append(": ").Append(YamlEscape(v.Version));
        }

        if (fm.References?.Count > 0)
        {
            sb.Append('\n').Append("references:");
            foreach (var r in fm.References)
                // Unescaped, a reference path containing ": " or starting with a YAML-special
                // character (e.g. one that happens to start with '#') broke the round trip
                // (ANALYSIS.md #93) — same escaping already applied to packages: above.
                sb.Append('\n').Append("  - ").Append(YamlEscape(r));
        }

        if (fm.Namespaces?.Count > 0)
        {
            sb.Append('\n').Append("namespaces:");
            foreach (var u in fm.Namespaces)
                sb.Append('\n').Append("  - ").Append(YamlEscape(u));
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
