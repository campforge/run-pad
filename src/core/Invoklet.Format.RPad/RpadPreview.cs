using System.Text;

namespace Invoklet.Format.RPad;

/// <summary>
/// The Markdown a script preview renders: the document as it would be saved, with the front matter
/// shown as a table of its settings instead of raw YAML.
/// </summary>
public static class RpadPreview
{
    /// <param name="code">The run block's current code (the editor text), in place of
    /// <see cref="RpadDocument.RunCode"/>, which is only updated on save.</param>
    public static string ToMarkdown(RpadDocument doc, string code) => ToMarkdown(doc, code, out _);

    /// <param name="code">The run block's current code (the editor text), in place of
    /// <see cref="RpadDocument.RunCode"/>, which is only updated on save.</param>
    /// <param name="codeLine">The zero-based line of the Markdown where the code's first line is, so
    /// that a line of the editor can be found in the preview.</param>
    public static string ToMarkdown(RpadDocument doc, string code, out int codeLine)
    {
        var sb = new StringBuilder();
        AppendFrontMatterTable(sb, doc.FrontMatter);
        sb.Append('\n');
        if (code.Length > 0 && !code.EndsWith('\n')) code += "\n";
        // A document built in code has no surrounding Markdown, so no run fence either (as in
        // RpadWriter.Write).
        var bare = doc.BodyBeforeCode.Length == 0 && doc.BodyAfterCode.Length == 0;
        if (bare) sb.Append("```").Append(doc.FrontMatter.Language.ToLanguageId()).Append(" run\n");
        else sb.Append(doc.BodyBeforeCode);
        codeLine = CountLines(sb);
        sb.Append(code);
        sb.Append(bare ? "```\n" : doc.BodyAfterCode);
        return sb.ToString();
    }

    // Markdown ends a line at \n, \r\n or a lone \r.
    private static int CountLines(StringBuilder sb)
    {
        var lines = 0;
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n' || (sb[i] == '\r' && (i + 1 == sb.Length || sb[i + 1] != '\n')))
                lines++;
        }
        return lines;
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
        if (fm.Arguments.Count > 0)
            Row(sb, "Declared arguments", string.Join(", ", fm.Arguments.Select(a =>
                $"{Code(a.Name)} ({Text(RpadArgument.TypeName(a.Type))})"
                + (a.Default is { } d ? " = " + string.Join(" ", RpadArgument.Items(d).Select(Code)) : ""))));
        foreach (var (name, set) in fm.ArgumentSets)
            Row(sb, $"Argument set {Text(name)}{(name == fm.SelectedArgumentSet ? " (selected)" : "")}",
                set.Count == 0 ? "defaults" : string.Join(", ", set.Select(v => $"{Code(v.Key)} = {string.Join(" ", RpadArgument.Items(v.Value).Select(Code))}")));
        if (fm.AllowUnsafe)
            Row(sb, "Unsafe code", "allowed");
        if (fm.Connections.Count > 0)
            Row(sb, "Connections", string.Join(", ", fm.Connections.Select(c => $"{Code(c.Name)} ({Text(c.Driver)})")));
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
