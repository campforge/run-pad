using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotPad.Format.RPad;

/// ToDo: надо радоьать со стримом, причем иметь возможность взять непосредственно стрим кода игонрируя заголовки
public static class RpadParser
{
    private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithTypeConverter(new TargetSdkYamlConverter())
        .WithTypeConverter(new PackageYamlConverter())
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
            fm = _yamlDeserializer.Deserialize<RpadFrontMatter>(yamlText);
            // YamlDotNet overwrites defaulted list properties with an explicit YAML null (e.g.
            // "references:" with no value) — restore the default in that case. TargetSdk/Package
            // don't need it — their converters below already map an empty scalar to
            // TargetSdk.Default/Package.Default themselves. For a non-nullable enum property with an
            // empty/null YAML value, YamlDotNet assigns default(TEnum) rather than leaving the
            // property at its object-initializer value, so Kind/Language need the same "restore to
            // the real default" treatment — their `Unknown = 0` member exists precisely to make that
            // distinguishable from a deliberately-chosen value.
            fm.Packages ??= [];
            fm.References ??= [];
            fm.Namespaces ??= [];
            if (fm.Kind == RpadKind.Unknown) fm.Kind = RpadKind.Statements;
            if (fm.Language == RpadLanguage.Unknown) fm.Language = RpadLanguage.CSharp;
        }
        catch
        {
            fm = new RpadFrontMatter();
        }

        var openFence = $"```{fm.Language.ToString().ToLowerInvariant()} run";
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

// TargetSdk/Package are plain YAML scalars ("net8.0", "2.1.66") in the .rpad file, not mappings —
// without these, YamlDotNet tries to bind the scalar onto the record's constructor and throws,
// which TryParse's outer catch turns into a silent reset of the *entire* front matter.
internal sealed class TargetSdkYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TargetSdk);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => TargetSdk.Parse(parser.Consume<Scalar>().Value);

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => emitter.Emit(new Scalar(((TargetSdk)value!).ToString()));
}

internal sealed class PackageYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Package);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var value = parser.Consume<Scalar>().Value;
        return string.IsNullOrEmpty(value) ? Package.Default : new Package(value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => emitter.Emit(new Scalar(((Package)value!).Version));
}
