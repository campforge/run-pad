using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RunPad.Format.RPad;

/// ToDo: figure out the stream handling — specifically, be able to grab the code stream directly, ignoring the headers
public static class RpadParser
{
    private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithTypeConverter(new TargetSdkYamlConverter())
        .WithTypeConverter(new PackageYamlConverter())
        .WithTypeConverter(new StrictEnumYamlConverter<RpadKind>(lowercase: false))
        .WithTypeConverter(new StrictEnumYamlConverter<RpadLanguage>(lowercase: true))
        .Build();

    /// <summary>
    /// Parses an .rpad file. Returns null if the content isn't a valid .rpad document — see
    /// <see cref="Parse"/> for the reason.
    /// </summary>
    public static RpadDocument? TryParse(string content) => Parse(content).Document;

    /// <summary>
    /// Parses an .rpad file, reporting why it isn't a valid document instead of guessing: missing
    /// front matter, malformed YAML (e.g. an unknown <c>kind:</c>) and a missing run block for the
    /// declared language are all errors. Absent/empty optional values still fall back to defaults.
    /// </summary>
    public static RpadParseResult Parse(string content)
    {
        // Delimiter lines must be exactly "---" (trailing whitespace allowed) — "---garbage" is neither
        // an opening nor a closing delimiter.
        var firstNl = content.IndexOf('\n');
        if (firstNl < 0 || !IsFrontMatterDelimiter(LineAt(content, 0)))
            return RpadParseResult.Failure("The file does not start with a '---' YAML front matter line.");
        var searchFrom = firstNl + 1;

        var fmEnd = FindLine(content, searchFrom, IsFrontMatterDelimiter);
        if (fmEnd < 0)
            return RpadParseResult.Failure("The YAML front matter is not closed by a '---' line.");

        var yamlText = content[searchFrom..fmEnd];
        var afterFm = SkipLine(content, fmEnd);
        var markdownBody = content[afterFm..];

        RpadFrontMatter fm;
        try
        {
            // Empty (or comment-only) YAML deserializes to null — every field is optional, so that's
            // just "all defaults", not an error.
            fm = _yamlDeserializer.Deserialize<RpadFrontMatter?>(yamlText) ?? new RpadFrontMatter();
            // YamlDotNet overwrites defaulted properties with an explicit YAML null (e.g.
            // "references:" with no value) — restore the default in that case. TargetSdk/Package
            // converters below already map an empty scalar to TargetSdk.Default/Package.Default
            // themselves. Kind/Language get `Unknown = 0` only for an empty/null value
            // (StrictEnumYamlConverter rejects an explicit "Unknown", "0" or any other undeclared
            // value), so Unknown here safely means "not specified" — restore the real default.
            fm.Version ??= "1.0";
            fm.TargetSdk ??= TargetSdk.Default;
            fm.Packages ??= [];
            fm.References ??= [];
            fm.Namespaces ??= [];
            if (fm.Kind == RpadKind.Unknown) fm.Kind = RpadKind.Statements;
            if (fm.Language == RpadLanguage.Unknown) fm.Language = RpadLanguage.CSharp;
        }
        catch (YamlException ex)
        {
            // Never fall back to a fresh RpadFrontMatter here — that silently swapped the
            // declared language/kind/packages/references for defaults, which a later save then
            // persisted. The YAML starts on the file's second line, hence the +1. The innermost
            // exception carries the plain reason (YamlDotNet prefixes its own message with marks).
            Exception inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            var detail = inner.Message;
            return RpadParseResult.Failure(
                $"Invalid YAML front matter (line {ex.Start.Line + 1}): {detail}");
        }
        catch (Exception ex)
        {
            return RpadParseResult.Failure($"Invalid YAML front matter: {ex.Message}");
        }

        var languageId = fm.Language.ToLanguageId();
        var openIdx = FindLine(markdownBody, 0, line => IsRunFence(line, languageId));
        if (openIdx < 0)
            return RpadParseResult.Failure($"No '```{languageId} run' code block found.");

        var codeStart = SkipLine(markdownBody, openIdx);
        // Checks the line at codeStart too — an empty block's closing fence sits right
        // there, and skipping it swallowed the fence and the Markdown after it into RunCode.
        var closeStart = FindLine(markdownBody, codeStart, IsClosingFence);

        if (closeStart < 0)
        {
            return RpadParseResult.Success(new RpadDocument
            {
                FrontMatter = fm,
                RunCode = markdownBody[codeStart..],
                BodyBeforeCode = markdownBody[..codeStart],
                BodyAfterCode = "",
            });
        }

        return RpadParseResult.Success(new RpadDocument
        {
            FrontMatter = fm,
            RunCode = markdownBody[codeStart..closeStart],   // includes trailing \n; "" for an empty block
            BodyBeforeCode = markdownBody[..codeStart],      // up to + including opening fence \n
            BodyAfterCode = markdownBody[closeStart..],      // from closing ``` onwards
        });
    }

    private delegate bool LineMatch(ReadOnlySpan<char> line);

    // Returns the start offset of the first line at or after `from` (itself a line start) whose
    // content, without the line break and trailing whitespace, satisfies `match`; -1 if none does.
    private static int FindLine(string text, int from, LineMatch match)
    {
        var lineStart = from;
        while (lineStart < text.Length)
        {
            if (match(LineAt(text, lineStart))) return lineStart;

            var nl = text.IndexOf('\n', lineStart);
            if (nl < 0) break;
            lineStart = nl + 1;
        }
        return -1;
    }

    // The line starting at lineStart, without its line break and trailing whitespace (incl. '\r').
    private static ReadOnlySpan<char> LineAt(string text, int lineStart)
    {
        var nl = text.IndexOf('\n', lineStart);
        return text.AsSpan(lineStart, (nl < 0 ? text.Length : nl) - lineStart).TrimEnd();
    }

    private static bool IsFrontMatterDelimiter(ReadOnlySpan<char> line) => line.SequenceEqual("---");

    // "```{languageId} run" exactly, as words: "```csharp runner", "```csharprun" and
    // "```csharp run extra" are not run blocks (the fence must be unambiguous).
    private static bool IsRunFence(ReadOnlySpan<char> line, string languageId)
    {
        if (!line.StartsWith("```")) return false;
        var info = line[3..];
        if (!info.StartsWith(languageId, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = info[languageId.Length..];
        return rest.Length > 0 && char.IsWhiteSpace(rest[0]) &&
               rest.TrimStart().Equals("run", StringComparison.OrdinalIgnoreCase);
    }

    // A closing fence is a run of 3+ backticks with nothing else on the line (CommonMark) — "```text"
    // opens a new block and "``` x" isn't a fence at all.
    private static bool IsClosingFence(ReadOnlySpan<char> line)
        => line.Length >= 3 && !line.ContainsAnyExcept('`');

    // Returns the offset of the first character of the next line (i.e. position after the \n at lineStart's end).
    private static int SkipLine(string text, int lineStart)
    {
        var nl = text.IndexOf('\n', lineStart);
        return nl >= 0 ? nl + 1 : text.Length;
    }

}

// Kind/Language: only a declared, non-Unknown member name is accepted (case-insensitively —
// `language: csharp` vs RpadLanguage.CSharp). YamlDotNet's own enum binding also accepted numbers
// ("99" → an undefined value, "0"/"Unknown" → silently Statements/CSharp later); those are now errors.
// An empty/null value yields Unknown, which RpadParser.Parse then replaces with the real default.
internal sealed class StrictEnumYamlConverter<TEnum>(bool lowercase) : IYamlTypeConverter
    where TEnum : struct, Enum
{
    public bool Accepts(Type type) => type == typeof(TEnum);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        var value = scalar.Value;
        if (value.Length == 0 || (scalar.Style == ScalarStyle.Plain && value is "~" or "null" or "Null" or "NULL"))
            return default(TEnum);

        var supported = Enum.GetValues<TEnum>().Where(v => Convert.ToInt32(v) != 0).ToList();
        foreach (var member in supported)
            if (string.Equals(member.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return member;

        var expected = string.Join(", ", supported.Select(Display));
        var message = $"Unsupported {typeof(TEnum).Name[4..].ToLowerInvariant()} '{value}'; expected one of: {expected}.";
        throw new YamlException(scalar.Start, scalar.End, message, new FormatException(message));
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => emitter.Emit(new Scalar(Display((TEnum)value!)));

    private string Display(TEnum member) => lowercase ? member.ToString().ToLowerInvariant() : member.ToString();
}

// TargetSdk/Package are plain YAML scalars ("net8.0", "2.1.66") in the .rpad file, not mappings —
// without these, YamlDotNet tries to bind the scalar onto the record's constructor and throws,
// which Parse would report as an invalid document.
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
