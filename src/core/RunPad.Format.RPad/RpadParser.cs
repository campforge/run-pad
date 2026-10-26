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
        .WithTypeConverter(new ConnectionYamlConverter())
        .WithTypeConverter(new NullableGuidYamlConverter())
        .Build();

    // The front matter's own keys, as RpadWriter writes them; any other top-level key is kept
    // verbatim in RpadFrontMatter.UnknownEntries.
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "version", "id", "language", "targetSdk", "kind", "packages", "references", "namespaces", "args",
        "argumentSets", "selectedArgumentSet", "allowUnsafe", "connections",
    };

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

        var originalYaml = content[searchFrom..fmEnd];
        var afterFm = SkipLine(content, fmEnd);
        var markdownBody = content[afterFm..];

        RpadFrontMatter fm;
        try
        {
            var yamlText = WithoutAliases(originalYaml);
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
            fm.Args ??= [];
            // "smoke:" with nothing after it is an empty set, like "smoke: []".
            fm.ArgumentSets = (fm.ArgumentSets ?? []).ToDictionary(s => s.Key, s => s.Value ?? [], StringComparer.Ordinal);
            if (string.IsNullOrEmpty(fm.SelectedArgumentSet)) fm.SelectedArgumentSet = null;
            fm.Connections ??= [];
            fm.UnknownEntries = UnknownEntries(yamlText);
            if (RepeatedArgumentSet(yamlText) is { } repeated)
                return RpadParseResult.Failure($"Invalid YAML front matter: the argument set '{repeated}' is defined twice.");
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

    // The front matter with every alias replaced by a copy of the node its anchor names, and no
    // anchors left; the text itself when it has no alias. Without this, an unknown entry's alias of an
    // anchor on a known key would point at nothing once RpadWriter rewrites that key from the model,
    // and the deserializer, which skips unknown keys, never sees an anchor declared in one, so a known
    // key's alias of it fails to load. A front matter with aliases loses its comments this way.
    private static string WithoutAliases(string yaml)
    {
        var events = new List<ParsingEvent>();
        var parser = new Parser(new StringReader(yaml));
        while (parser.MoveNext()) events.Add(parser.Current!);
        if (!events.Any(e => e is AnchorAlias)) return yaml;

        var anchored = new Dictionary<AnchorName, List<ParsingEvent>>();
        var recording = new List<(AnchorName Anchor, int Depth, List<ParsingEvent> Events)>();
        var output = new List<ParsingEvent>();
        var depth = 0;
        foreach (var e in events)
        {
            if (e is AnchorAlias alias)
            {
                if (!anchored.TryGetValue(alias.Value, out var copy))
                    throw new YamlException(alias.Start, alias.End, $"The alias *{alias.Value} names no anchor before it.");
                foreach (var c in copy) Put(c);
                continue;
            }

            var (plain, anchor) = WithoutAnchor(e);
            if (!anchor.IsEmpty && plain is not Scalar) recording.Add((anchor, depth, []));
            Put(plain);
            if (!anchor.IsEmpty && plain is Scalar) anchored[anchor] = [plain];
            depth += e.NestingIncrease;
            if (e.NestingIncrease < 0 && recording.Count > 0 && recording[^1].Depth == depth)
            {
                anchored[recording[^1].Anchor] = recording[^1].Events;
                recording.RemoveAt(recording.Count - 1);
            }
        }

        var writer = new StringWriter();
        var emitter = new Emitter(writer);
        foreach (var e in output) emitter.Emit(e);
        return writer.ToString();

        void Put(ParsingEvent e)
        {
            output.Add(e);
            foreach (var (_, _, list) in recording) list.Add(e);
        }
    }

    private static (ParsingEvent Event, AnchorName Anchor) WithoutAnchor(ParsingEvent e) => e switch
    {
        Scalar { Anchor.IsEmpty: false } s => (new Scalar(AnchorName.Empty, s.Tag, s.Value, s.Style, s.IsPlainImplicit, s.IsQuotedImplicit), s.Anchor),
        SequenceStart { Anchor.IsEmpty: false } s => (new SequenceStart(AnchorName.Empty, s.Tag, s.IsImplicit, s.Style), s.Anchor),
        MappingStart { Anchor.IsEmpty: false } m => (new MappingStart(AnchorName.Empty, m.Tag, m.IsImplicit, m.Style), m.Anchor),
        _ => (e, AnchorName.Empty),
    };

    // Each top-level entry with an unknown key, from its key up to the next key: its value and the
    // comments between them, without trailing blank lines, LF line breaks. A block scalar that keeps
    // its final line breaks ("|+", ">+") keeps them, less the one the writer puts back after it.
    //
    // Walks the parser's events rather than loading a YamlStream: the representation model rejects
    // a repeated key, which the deserializer accepts (the last value wins), so a hand-edited or
    // merged front matter with "kind:" twice would otherwise stop opening at all. A repeated unknown
    // key likewise keeps only its last entry, so a save does not write the key twice.
    private static List<string> UnknownEntries(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _) || !parser.TryConsume<MappingStart>(out var root))
            return [];

        var keys = new List<(string? Name, int Start, int ValueEnd, bool KeepsLineBreaks)>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            // A key that isn't a scalar (a "? [a, b]" complex key) is never one of ours, so it is
            // kept as unknown, with a null name.
            var name = parser.Current is Scalar scalar ? scalar.Value : null;
            var start = (int)parser.Current!.Start.Index;
            SkipNode(parser, yaml);
            var keeps = parser.Current is Scalar { Style: ScalarStyle.Literal or ScalarStyle.Folded } value
                        && KeepsFinalLineBreaks(yaml, (int)value.Start.Index);
            keys.Add((name, start, SkipNode(parser, yaml), keeps));
        }

        var entries = new List<(string? Name, string Text)>();
        for (var i = 0; i < keys.Count; i++)
        {
            var (name, start, valueEnd, keeps) = keys[i];
            if (name is not null && KnownKeys.Contains(name)) continue;
            // A flow mapping ("{version: '1.0', future: 3}") has no lines to take whole: keep just
            // the key and its value, which read back the same way as a block-style entry.
            var end = root.Style == MappingStyle.Flow ? valueEnd
                : i + 1 < keys.Count ? keys[i + 1].Start : yaml.Length;
            var text = yaml[start..end].Replace("\r\n", "\n");
            text = keeps ? (text.EndsWith('\n') ? text[..^1] : text) : text.TrimEnd();
            if (name is not null) entries.RemoveAll(e => e.Name == name);
            entries.Add((name, text));
        }
        return [.. entries.Select(e => e.Text)];
    }

    // The first set name argumentSets: repeats, or null. YAML reads a repeated key as its last value
    // only, so a save would silently drop the other set.
    private static string? RepeatedArgumentSet(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _) || !parser.TryConsume<MappingStart>(out _)) return null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var isSets = parser.Current is Scalar { Value: "argumentSets" };
            SkipNode(parser, yaml);
            if (!isSets || !parser.TryConsume<MappingStart>(out _))
            {
                SkipNode(parser, yaml);
                continue;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (parser.Current is Scalar { Value: var name } && !names.Add(name)) return name;
                SkipNode(parser, yaml);
                SkipNode(parser, yaml);
            }
        }
        return null;
    }

    // Whether the block scalar header at `at` ("|+", ">2+", "|+ # note") asks to keep final line breaks.
    private static bool KeepsFinalLineBreaks(string yaml, int at)
    {
        var lineEnd = yaml.IndexOf('\n', at);
        var header = yaml[at..(lineEnd < 0 ? yaml.Length : lineEnd)];
        var comment = header.IndexOf(" #", StringComparison.Ordinal);
        return (comment < 0 ? header : header[..comment]).Contains('+');
    }

    // Consumes one node with everything nested in it; returns the offset just past its end.
    private static int SkipNode(IParser parser, string yaml)
    {
        var depth = 0;
        ParsingEvent current;
        do
        {
            current = parser.Consume<ParsingEvent>();
            depth += current.NestingIncrease;
        }
        while (depth > 0);
        // The end event of a flow collection is marked at its closing bracket, not past it.
        var end = (int)current.End.Index;
        return current is MappingEnd or SequenceEnd && end < yaml.Length && yaml[end] is '}' or ']'
            ? end + 1
            : end;
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

// A connection is name, driver and params, with params holding any YAML shape; other keys are kept
// in Extra. Every scalar is read as text, so "30" and "true" stay what the file says.
internal sealed class ConnectionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(RpadConnection);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var start = parser.Current!.Start;
        if (!parser.TryConsume<MappingStart>(out _))
            throw new YamlException(start, start, "A connection must be a mapping with name, driver and params.");
        var connection = new RpadConnection();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            var value = ReadValue(parser);
            switch (key)
            {
                case "name": connection.Name = value.Text ?? throw NotText(key, start); break;
                case "driver": connection.Driver = value.Text ?? throw NotText(key, start); break;
                case "params":
                    if (value.Fields is { } fields) connection.Params = fields;
                    else if (value.Text is not { Length: > 0 }) connection.Params = new(StringComparer.Ordinal);
                    else throw new YamlException(start, start, "A connection's params must be a mapping.");
                    break;
                default: (connection.Extra ??= new(StringComparer.Ordinal))[key] = value; break;
            }
        }
        return connection;
    }

    private static YamlException NotText(string key, Mark at) => new(at, at, $"A connection's {key} must be text.");

    internal static RpadValue ReadValue(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar)) return RpadValue.Of(scalar.Value);
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var items = new List<RpadValue>();
            while (!parser.TryConsume<SequenceEnd>(out _)) items.Add(ReadValue(parser));
            return RpadValue.List(items);
        }
        if (parser.TryConsume<MappingStart>(out _))
        {
            var fields = new Dictionary<string, RpadValue>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                fields[key] = ReadValue(parser);
            }
            return new RpadValue { Fields = fields };
        }
        var at = parser.Current!.Start;
        throw new YamlException(at, at, "Anchors and aliases are not supported in connections.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => throw new NotSupportedException("RpadWriter writes connections.");
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

internal sealed class NullableGuidYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Guid) || type == typeof(Guid?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var value = parser.Consume<Scalar>().Value;
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => emitter.Emit(new Scalar(value is Guid g ? g.ToString("D") : ""));
}
