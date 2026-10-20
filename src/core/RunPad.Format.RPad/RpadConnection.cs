using System.Text;
using System.Text.Json.Serialization;

namespace RunPad.Format.RPad;

/// <summary>
/// One entry of the front matter's <c>connections:</c> list: a database, HTTP or gRPC endpoint the
/// script uses, named for the script's code (<c>Connections.Orders</c> for <c>orders</c>). Only the
/// name and the driver are fixed; everything else belongs to the driver, in <see cref="Params"/>.
/// </summary>
public sealed class RpadConnection
{
    public string Name { get; set; } = "";

    /// <summary>The connection type: <c>postgres</c>, <c>sqlite</c>, <c>http</c>, <c>grpc</c>…</summary>
    public string Driver { get; set; } = "";

    /// <summary>
    /// What the driver needs — a connection string, an address, headers… — in the file's order.
    /// Text may name a secret or an environment variable (see <see cref="RpadPlaceholders"/>),
    /// which is substituted only when the script runs.
    /// </summary>
    public Dictionary<string, RpadValue> Params { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys of the entry other than name, driver and params, kept as they are so a newer
    /// RunPad's additions survive a save.</summary>
    public Dictionary<string, RpadValue>? Extra { get; set; }

    /// <summary>The script's name for the connection: words of <see cref="Name"/> (split at
    /// <c>-</c>, <c>_</c> and <c>.</c>) in PascalCase, so <c>orders-db</c> is <c>OrdersDb</c>.</summary>
    public static string CodeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var word in name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
        return sb.ToString();
    }

    private const string ReservedCodeName = "Connections";

    /// <summary>
    /// Problems with a connections list, one message each: a name that is missing, not a valid name
    /// (a letter, then letters, digits, <c>-</c>, <c>_</c> or <c>.</c>) or used twice — also when
    /// two names give the same <see cref="CodeName"/> —, a name whose code name is the reserved
    /// <c>Connections</c>, a missing driver, or a malformed placeholder. Empty when the list is fine.
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<RpadConnection> connections)
    {
        var errors = new List<string>();
        var codeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in connections)
        {
            var name = connection.Name;
            if (string.IsNullOrWhiteSpace(name))
                errors.Add("A connection has no name.");
            else if (!IsValidName(name))
                errors.Add($"'{name}' is not a valid connection name: start with a letter, then use letters, digits, '-', '_' or '.'.");
            // The generated class that holds every connection is itself named Connections.
            else if (CodeName(name) == ReservedCodeName)
                errors.Add($"Connection '{name}': the code name {ReservedCodeName} is reserved.");
            else if (codeNames.TryGetValue(CodeName(name), out var other))
                errors.Add(string.Equals(other, name, StringComparison.OrdinalIgnoreCase)
                    ? $"Two connections are named '{name}'."
                    : $"Connections '{other}' and '{name}' would have the same name in code, {CodeName(name)}.");
            else
                codeNames[CodeName(name)] = name;

            var label = string.IsNullOrWhiteSpace(name) ? "A connection" : $"Connection '{name}'";
            if (string.IsNullOrWhiteSpace(connection.Driver))
                errors.Add($"{label} has no driver.");
            foreach (var (key, value) in connection.Params)
                foreach (var text in value.Texts())
                    if (RpadPlaceholders.Parse(text).Error is { } error)
                        errors.Add($"{label}, {key}: {error}");
        }
        return errors;
    }

    // Starting with a letter keeps CodeName a C# and VB identifier; the separators only split words.
    private static bool IsValidName(string name) =>
        char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>
/// A value under <c>params:</c>: text, a list or a map — exactly one of <see cref="Text"/>,
/// <see cref="Items"/> and <see cref="Fields"/>. Every YAML scalar is text (<c>30</c> and
/// <c>true</c> included); the driver decides what it means.
/// </summary>
public sealed class RpadValue
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RpadValue>? Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RpadValue>? Fields { get; init; }

    public static RpadValue Of(string text) => new() { Text = text };

    public static RpadValue List(IEnumerable<RpadValue> items) => new() { Items = [.. items] };

    public static RpadValue Map(IEnumerable<KeyValuePair<string, RpadValue>> fields) =>
        new() { Fields = new Dictionary<string, RpadValue>(fields, StringComparer.Ordinal) };

    /// <summary>Every piece of text in this value, nested ones included.</summary>
    public IEnumerable<string> Texts()
    {
        if (Text is not null) yield return Text;
        foreach (var item in Items ?? [])
            foreach (var text in item.Texts())
                yield return text;
        foreach (var field in Fields?.Values ?? Enumerable.Empty<RpadValue>())
            foreach (var text in field.Texts())
                yield return text;
    }

    internal RpadValue Clone() => new()
    {
        Text = Text,
        Items = Items?.Select(i => i.Clone()).ToList(),
        Fields = Fields?.ToDictionary(f => f.Key, f => f.Value.Clone(), StringComparer.Ordinal),
    };
}

/// <summary>
/// Substitutions in connection text: <c>${secret:name}</c> is a secret from RunPad's Secret Store,
/// <c>${env:NAME}</c> an environment variable, and <c>$${</c> a literal <c>${</c>. They are
/// replaced only when the script runs, never in the file, the build or its cache.
/// </summary>
public static class RpadPlaceholders
{
    public enum Kind { Text, Secret, Environment }

    public readonly record struct Part(Kind Kind, string Value);

    public sealed record Result(IReadOnlyList<Part> Parts, string? Error);

    public static Result Parse(string text)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 2 < text.Length && text[i + 1] == '$' && text[i + 2] == '{')
            {
                literal.Append("${");
                i += 2;
                continue;
            }
            if (text[i] != '$' || i + 1 >= text.Length || text[i + 1] != '{')
            {
                literal.Append(text[i]);
                continue;
            }
            var end = text.IndexOf('}', i + 2);
            if (end < 0) return new Result(parts, $"'${{' at position {i + 1} is not closed by '}}' (write '$${{' for a literal '${{').");
            var body = text[(i + 2)..end];
            var colon = body.IndexOf(':');
            var kind = colon < 0 ? null : body[..colon];
            var name = colon < 0 ? "" : body[(colon + 1)..];
            var parsed = kind switch
            {
                "secret" => Kind.Secret,
                "env" => Kind.Environment,
                _ => (Kind?)null,
            };
            if (parsed is null)
                return new Result(parts, $"'${{{body}}}' is not a placeholder: use ${{secret:name}} or ${{env:NAME}}.");
            if (name.Trim().Length == 0)
                return new Result(parts, $"'${{{body}}}' names no {(parsed == Kind.Secret ? "secret" : "variable")}.");
            if (literal.Length > 0) parts.Add(new Part(Kind.Text, literal.ToString()));
            literal.Clear();
            parts.Add(new Part(parsed.Value, name.Trim()));
            i = end;
        }
        if (literal.Length > 0) parts.Add(new Part(Kind.Text, literal.ToString()));
        return new Result(parts, null);
    }
}
