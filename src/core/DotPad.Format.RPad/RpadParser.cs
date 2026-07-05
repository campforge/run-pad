using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotPad.Format.RPad;

public static class RpadParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses an .rpad file. Returns null if the file does not start with YAML front matter.
    /// </summary>
    public static RpadDocument? TryParse(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal) &&
            !content.StartsWith("---\r\n", StringComparison.Ordinal))
            return null;

        var firstNl = content.IndexOf('\n');
        if (firstNl < 0) return null;
        var searchFrom = firstNl + 1;

        var fmEnd = FindLineStart(content, searchFrom, "---");
        if (fmEnd < 0) return null;

        var yamlText = content[searchFrom..fmEnd];
        var afterFm = SkipLine(content, fmEnd);
        var markdownBody = content[afterFm..];

        RpadFrontMatter fm;
        try
        {
            fm = YamlDeserializer.Deserialize<RpadFrontMatter>(yamlText) ?? new RpadFrontMatter();
            // YamlDotNet overwrites defaulted string properties with an explicit YAML null
            // (e.g. "language:" with no value) — restore the defaults in that case.
            fm.Language ??= "csharp";
            fm.TargetFramework ??= "net10.0";
            fm.Kind ??= "Statements";
            fm.Packages ??= [];
            fm.References ??= [];
            fm.Usings ??= [];
        }
        catch
        {
            fm = new RpadFrontMatter();
        }

        var openFence = $"```{fm.Language.ToLowerInvariant()} run";
        var openIdx = FindLineStart(markdownBody, 0, openFence);
        if (openIdx < 0)
        {
            return new RpadDocument
            {
                FrontMatter = fm,
                RunCode = "",
                BodyBeforeCode = markdownBody,
                BodyAfterCode = "",
            };
        }

        var codeStart = SkipLine(markdownBody, openIdx);
        var closeNl = FindClosingFence(markdownBody, codeStart);

        if (closeNl < 0)
        {
            return new RpadDocument
            {
                FrontMatter = fm,
                RunCode = markdownBody[codeStart..],
                BodyBeforeCode = markdownBody[..codeStart],
                BodyAfterCode = "",
            };
        }

        // closeNl is the \n that ends the last line of code; closeNl+1 is the start of ```
        return new RpadDocument
        {
            FrontMatter = fm,
            RunCode = markdownBody[codeStart..(closeNl + 1)],   // includes trailing \n
            BodyBeforeCode = markdownBody[..codeStart],          // up to + including opening fence \n
            BodyAfterCode = markdownBody[(closeNl + 1)..],       // from closing ``` onwards
        };
    }

    // Returns the start offset of the first line (at or after `from`) that begins with `token`.
    private static int FindLineStart(string text, int from, string token)
    {
        var idx = from;
        while (idx <= text.Length)
        {
            if (idx + token.Length <= text.Length &&
                text.AsSpan(idx, token.Length).Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return idx;

            var nl = text.IndexOf('\n', idx);
            if (nl < 0) break;
            idx = nl + 1;
        }
        return -1;
    }

    // Returns the offset of the first character of the next line (i.e. position after the \n at lineStart's end).
    private static int SkipLine(string text, int lineStart)
    {
        var nl = text.IndexOf('\n', lineStart);
        return nl >= 0 ? nl + 1 : text.Length;
    }

    // Finds the \n that ends the last line of code, just before the closing ``` line.
    // Returns -1 if not found.
    private static int FindClosingFence(string text, int from)
    {
        var idx = from;
        while (idx < text.Length)
        {
            var nl = text.IndexOf('\n', idx);
            if (nl < 0) break;

            var lineStart = nl + 1;
            if (lineStart + 3 <= text.Length &&
                text[lineStart] == '`' && text[lineStart + 1] == '`' && text[lineStart + 2] == '`')
            {
                var afterFence = lineStart + 3;
                if (afterFence >= text.Length ||
                    text[afterFence] == '\n' || text[afterFence] == '\r' ||
                    text[afterFence] == ' ' || text[afterFence] == '\t')
                    return nl;  // \n that ends the last code line
            }
            idx = lineStart;
        }
        return -1;
    }
}
