using System.Text;

namespace RunPad.Format.RPad;

/// <summary>
/// The Markdown a script preview renders: the document as it would be saved, with the front matter
/// shown as a table of its settings instead of raw YAML.
/// </summary>
public static class RpadPreview
{
    /// <param name="code">The run block's current code (the editor text), in place of
    /// <see cref="RpadDocument.RunCode"/>, which is only updated on save.</param>
    public static string ToMarkdown(RpadDocument doc, string code)
    {
        var sb = new StringBuilder();
        AppendFrontMatterTable(sb, doc.FrontMatter);
        sb.Append('\n');
        if (code.Length > 0 && !code.EndsWith('\n')) code += "\n";
        // A document built in code has no surrounding Markdown, so no run fence either (as in
        // RpadWriter.Write).
        if (doc.BodyBeforeCode.Length == 0 && doc.BodyAfterCode.Length == 0)
            sb.Append("```").Append(doc.FrontMatter.Language.ToLanguageId()).Append(" run\n").Append(code).Append("```\n");
        else
            sb.Append(doc.BodyBeforeCode).Append(code).Append(doc.BodyAfterCode);
        return sb.ToString();
    }

    private static void AppendFrontMatterTable(StringBuilder sb, RpadFrontMatter fm)
    {
        sb.Append("| Setting | Value |\n| --- | --- |\n");
        Row(sb, "Language", fm.Language switch
        {
            RpadLanguage.CSharp => "C#",
            RpadLanguage.Vb => "Visual Basic",
            var other => other.ToString(),
        });
        Row(sb, "Kind", fm.Kind.ToString());
        Row(sb, "Target SDK", Text(fm.TargetSdk.ToString()));
        if (fm.Packages.Count > 0)
            Row(sb, "Packages", string.Join(", ", fm.Packages.Select(p => Code($"{p.Key} {p.Value.Version}"))));
        if (fm.References is { Count: > 0 } references)
            Row(sb, "References", string.Join(", ", references.Select(Code)));
        if (fm.Namespaces is { Count: > 0 } namespaces)
            Row(sb, "Namespaces", string.Join(", ", namespaces.Select(Code)));
        if (fm.Args is { Count: > 0 } args)
            Row(sb, "Arguments", string.Join(", ", args.Select(Code)));
    }

    // The value is Markdown already: fixed text, Text() or Code().
    private static void Row(StringBuilder sb, string name, string value)
        => sb.Append("| ").Append(name).Append(" | ").Append(value).Append(" |\n");

    // Text from the file as literal Markdown in a table cell: every ASCII punctuation character
    // escaped (CommonMark allows a backslash before any of them), line breaks folded into spaces.
    private static string Text(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\r' or '\n') sb.Append(' ');
            else
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is > ' ' and < (char)127) sb.Append('\\');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // A code span that survives any backticks in the value (CommonMark: a longer backtick run
    // than any inside, padded with spaces). Markdig reads code spans before it splits a table row,
    // so a pipe inside one needs no escape; a line break would still end the row.
    private static string Code(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        var longest = 0;
        for (int i = 0, run = 0; i < value.Length; i++)
        {
            run = value[i] == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', longest + 1);
        return $"{fence} {value} {fence}";
    }
}
