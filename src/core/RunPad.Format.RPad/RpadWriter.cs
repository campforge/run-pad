using System.Text;
using System.Text.Json;

namespace RunPad.Format.RPad;

public static class RpadWriter
{
    /// ToDo: figure out the stream handling — specifically, be able to grab the code stream directly, ignoring the headers
    /// <summary>Serializes the document back to .rpad file content.</summary>
    public static string Write(RpadDocument doc) => Write(doc, OperatingSystem.IsWindows());

    /// <param name="windowsPaths">Whether <c>\</c> in a reference can only be a separator (see ReferencePath.Canonical).</param>
    public static string Write(RpadDocument doc, bool windowsPaths)
    {
        var yaml = SerializeFm(doc.FrontMatter, windowsPaths);
        // A truly empty block stays empty rather than gaining a blank code line; the
        // closing fence already starts its own line after the opening fence's newline.
        var code = doc.RunCode.Length == 0 || doc.RunCode.EndsWith('\n') ? doc.RunCode : doc.RunCode + "\n";
        // A document built in code (not parsed) has no surrounding Markdown — and so no run fence,
        // which RpadParser requires; emit a bare one rather than writing unfenced code.
        if (doc.BodyBeforeCode.Length == 0 && doc.BodyAfterCode.Length == 0)
            return $"---\n{yaml}\n---\n```{doc.FrontMatter.Language.ToLanguageId()} run\n{code}```\n";
        return $"---\n{yaml}\n---\n{doc.BodyBeforeCode}{code}{doc.BodyAfterCode}";
    }

    // A new .rpad document around <code>: front matter for the language/kind, a title heading and
    // the run block. The code is whatever the caller wants the script to start with — the language
    // decides that (ILanguageHostFactory.CodeForKind), not the file format.
    public static string NewFile(
        string code,
        string title = "Untitled",
        string targetSdk = "latest",
        RpadKind kind = RpadKind.Statements,
        RpadLanguage language = RpadLanguage.CSharp)
    {
        var languageId = language.ToLanguageId();

        // Even an empty code block needs its own line: without a trailing newline the closing fence
        // lands on the code's line, and RpadParser.FindClosingFence only recognizes a fence that
        // starts a line. Write() applies the same guard to RunCode.
        if (!code.EndsWith('\n')) code += "\n";
        return $"---\nversion: \"1.0\"\nlanguage: {languageId}\ntargetSdk: {targetSdk}\nkind: {kind}\n---\n\n# {title}\n\n```{languageId} run\n{code}```\n";
    }

    private static string SerializeFm(RpadFrontMatter fm, bool windowsPaths)
    {
        // Explicit '\n' throughout, not StringBuilder.AppendLine (which appends the *running
        // machine's* Environment.NewLine) — otherwise the very same document produced identical
        // byte-for-byte on Linux/macOS came out with CRLF front matter on Windows, needlessly
        // diverging from Write()'s own literal "\n" fences around it.
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
                // — same escaping already applied to packages: above.
                sb.Append('\n').Append("  - ").Append(YamlEscape(ReferencePath.Canonical(r, windowsPaths)));
        }

        if (fm.Namespaces?.Count > 0)
        {
            sb.Append('\n').Append("namespaces:");
            foreach (var u in fm.Namespaces)
                sb.Append('\n').Append("  - ").Append(YamlEscape(u));
        }

        if (fm.Args?.Count > 0)
        {
            sb.Append('\n').Append("args:");
            foreach (var a in fm.Args)
                sb.Append('\n').Append("  - ").Append(YamlEscape(a));
        }

        return sb.ToString();
    }

    // Quoting every string avoids YAML's plain-scalar indicators and implicit null values.
    // JSON double-quoted strings are also YAML scalars and preserve line breaks/control characters,
    // which YAML single-quoted multiline scalars would fold into spaces.
    private static string YamlEscape(string value) => JsonSerializer.Serialize(value);
}
