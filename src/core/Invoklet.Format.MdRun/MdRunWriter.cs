using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Invoklet.Format.MdRun;

public static partial class MdRunWriter
{
    /// ToDo: figure out the stream handling — specifically, be able to grab the code stream directly, ignoring the headers
    /// <summary>Serializes the document back to .inv file content.</summary>
    public static string Write(MdRunDocument doc) => Write(doc, OperatingSystem.IsWindows());

    /// <param name="windowsPaths">Whether <c>\</c> in a reference can only be a separator (see ReferencePath.Canonical).</param>
    public static string Write(MdRunDocument doc, bool windowsPaths)
    {
        var yaml = SerializeFm(doc.FrontMatter, windowsPaths);
        // A truly empty block stays empty rather than gaining a blank code line; the
        // closing fence already starts its own line after the opening fence's newline.
        var code = doc.RunCode.Length == 0 || doc.RunCode.EndsWith('\n') ? doc.RunCode : doc.RunCode + "\n";
        // A document built in code (not parsed) has no surrounding Markdown — and so no run fence,
        // which MdRunParser requires; emit a bare one rather than writing unfenced code.
        if (doc.BodyBeforeCode.Length == 0 && doc.BodyAfterCode.Length == 0)
            return $"---\n{yaml}\n---\n```{doc.FrontMatter.Language.ToLanguageId()} run\n{code}```\n";
        return $"---\n{yaml}\n---\n{doc.BodyBeforeCode}{code}{doc.BodyAfterCode}";
    }

    // A new .inv document around <code>: front matter for the language/kind, a title heading and
    // the run block. The code is whatever the caller wants the script to start with — the language
    // decides that (ILanguageHostFactory.CodeForKind), not the file format.
    public static string NewFile(
        string code,
        string title = "Untitled",
        string targetSdk = "latest",
        MdRunKind kind = MdRunKind.Statements,
        MdRunLanguage language = MdRunLanguage.CSharp)
    {
        var languageId = language.ToLanguageId();

        // Even an empty code block needs its own line: without a trailing newline the closing fence
        // lands on the code's line, and MdRunParser.FindClosingFence only recognizes a fence that
        // starts a line. Write() applies the same guard to RunCode.
        if (!code.EndsWith('\n')) code += "\n";
        var id = Guid.NewGuid().ToString("D");
        return $"---\nversion: \"1.0\"\nid: {id}\nlanguage: {languageId}\ntargetSdk: {targetSdk}\nkind: {kind}\n---\n\n# {title}\n\n```{languageId} run\n{code}```\n";
    }

    private static string SerializeFm(MdRunFrontMatter fm, bool windowsPaths)
    {
        // Explicit '\n' throughout, not StringBuilder.AppendLine (which appends the *running
        // machine's* Environment.NewLine) — otherwise the very same document produced identical
        // byte-for-byte on Linux/macOS came out with CRLF front matter on Windows, needlessly
        // diverging from Write()'s own literal "\n" fences around it.
        var sb = new StringBuilder();
        // Connections came with format 1.1; a reader that knows only 1.0 can tell from the version.
        sb.Append("version: ").Append(fm.Connections.Count > 0 && fm.Version == "1.0" ? "1.1" : fm.Version).Append('\n');
        if (fm.Id is { } id) sb.Append("id: ").Append(id.ToString("D")).Append('\n');
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

        if (fm.Arguments.Count > 0)
        {
            sb.Append('\n').Append("arguments:");
            foreach (var argument in fm.Arguments)
            {
                sb.Append('\n').Append("  - name: ").Append(Scalar(argument.Name));
                sb.Append('\n').Append("    type: ").Append(MdRunArgument.TypeName(argument.Type)); // fixed names, plain in YAML
                if (argument.Default is { } value) WriteEntry(sb, "default", value, 4);
                if (argument.Type == MdRunArgumentType.Enum)
                    WriteEntry(sb, "values", MdRunValue.List(argument.Values.Select(MdRunValue.Of)), 4);
            }
        }

        if (fm.ArgumentSets.Count > 0)
        {
            sb.Append('\n').Append("argumentSets:");
            foreach (var (name, values) in fm.ArgumentSets)
            {
                sb.Append('\n').Append("  ").Append(Scalar(name)).Append(':');
                if (values.Count == 0) sb.Append(" {}");
                foreach (var (key, value) in values) WriteEntry(sb, key, value, 4);
            }
        }

        if (fm.SelectedArgumentSet is { } selected)
            sb.Append('\n').Append("selectedArgumentSet: ").Append(Scalar(selected));

        // Written only when on, so existing documents stay byte-for-byte unchanged.
        if (fm.AllowUnsafe) sb.Append('\n').Append("allowUnsafe: true");

        if (fm.Connections.Count > 0)
        {
            sb.Append('\n').Append("connections:");
            foreach (var connection in fm.Connections)
            {
                sb.Append('\n').Append("  - name: ").Append(Scalar(connection.Name));
                sb.Append('\n').Append("    driver: ").Append(Scalar(connection.Driver));
                if (connection.Params.Count > 0)
                {
                    sb.Append('\n').Append("    params:");
                    foreach (var (key, value) in connection.Params) WriteEntry(sb, key, value, 6);
                }
                foreach (var (key, value) in connection.Extra ?? []) WriteEntry(sb, key, value, 4);
            }
        }

        // Keys only a newer Invoklet knows, as they were read.
        foreach (var entry in fm.UnknownEntries)
            sb.Append('\n').Append(entry.Replace("\r\n", "\n"));

        return sb.ToString();
    }

    // A map entry in block style. A list of text is a block list; anything else in a list is
    // written as a JSON flow collection, which YAML reads the same way.
    private static void WriteEntry(StringBuilder sb, string key, MdRunValue value, int indent)
    {
        sb.Append('\n').Append(' ', indent).Append(Scalar(key)).Append(':');
        if (value.Text is { } text)
            sb.Append(' ').Append(Scalar(text));
        else if (value.Items is { } items)
        {
            if (items.Count == 0) sb.Append(" []");
            foreach (var item in items)
                sb.Append('\n').Append(' ', indent + 2).Append("- ").Append(item.Text is { } t ? Scalar(t) : Flow(item));
        }
        else if (value.Fields is { Count: > 0 } fields)
            foreach (var (k, v) in fields) WriteEntry(sb, k, v, indent + 2);
        else
            sb.Append(" {}");
    }

    private static string Flow(MdRunValue value) =>
        value.Text is { } text ? Quoted(text)
        : value.Items is { } items ? "[" + string.Join(", ", items.Select(Flow)) + "]"
        : "{" + string.Join(", ", (value.Fields ?? []).Select(f => Quoted(f.Key) + ": " + Flow(f.Value))) + "}";

    // Connection text plain where YAML reads it back unchanged as text (names, drivers, numbers,
    // simple words), double-quoted otherwise: connection strings, URLs, placeholders, empty text.
    private static string Scalar(string value) =>
        PlainScalar().IsMatch(value) && value is not ("null" or "Null" or "NULL") ? value : Quoted(value);

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex PlainScalar();

    // Readable non-ASCII text, still a valid YAML double-quoted scalar.
    private static readonly JsonSerializerOptions QuotedOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Quoted(string value) => JsonSerializer.Serialize(value, QuotedOptions);

    // Quoting every string avoids YAML's plain-scalar indicators and implicit null values.
    // JSON double-quoted strings are also YAML scalars and preserve line breaks/control characters,
    // which YAML single-quoted multiline scalars would fold into spaces.
    private static string YamlEscape(string value) => JsonSerializer.Serialize(value);
}
