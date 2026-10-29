using System.Text.Json;

namespace Invoklet.Format.RPad;

/// <summary>The immutable, language-neutral argument contract handed to a script host.</summary>
public sealed record RpadArgumentContract(IReadOnlyList<RpadArgumentContractMember> Members)
{
    public static RpadArgumentContract Empty { get; } = new([]);

    /// <summary>Builds a contract from the selected set. Invalid metadata returns an empty contract
    /// and all diagnostics, so opening the document never depends on successful conversion.</summary>
    public static RpadArgumentContract Build(RpadFrontMatter? frontMatter, out IReadOnlyList<string> errors,
        IReadOnlyList<string>? arguments = null, string? argumentSet = null)
    {
        if (frontMatter is null)
        {
            errors = [];
            return Empty;
        }

        var problems = new List<string>(RpadArguments.ValidateSchema(frontMatter));
        var resolved = RpadArguments.Resolve(frontMatter, arguments, argumentSet);
        if (resolved.Error is { } error) problems.Add(error);
        var overrides = NamedOverrides(arguments, frontMatter.Arguments);
        Dictionary<string, RpadValue>? selected = null;
        if (arguments is null && (argumentSet ?? frontMatter.SelectedArgumentSet) is { } selectedName)
            frontMatter.ArgumentSets.TryGetValue(selectedName, out selected);
        var members = new List<RpadArgumentContractMember>();
        foreach (var declaration in frontMatter.Arguments)
        {
            var value = selected is not null && selected.TryGetValue(declaration.Name, out var selectedValue)
                ? selectedValue
                : overrides.TryGetValue(declaration.Name, out var overrideValue) ? overrideValue : declaration.Default;
            if (value is not null && declaration.Check(value) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            members.Add(new RpadArgumentContractMember(
                declaration.Name,
                RpadArgument.TypeName(declaration.Type),
                value is null ? [] : RpadArgument.Items(value).ToArray(),
                declaration.Type == RpadArgumentType.Enum ? declaration.Values : [],
                IsNullable: declaration.Default is null));
        }

        errors = problems;
        return problems.Count == 0 ? new(members) : Empty;
    }

    private static Dictionary<string, RpadValue> NamedOverrides(
        IReadOnlyList<string>? args, IReadOnlyList<RpadArgument> declarations)
    {
        var names = declarations.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, RpadValue>(StringComparer.Ordinal);
        if (args is null) return values;
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2) continue;
            var name = token[2..];
            if (!names.Contains(name) || i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                continue;
            var item = args[++i];
            if (declarations.Any(d => d.Name == name && d.Type == RpadArgumentType.StringList))
            {
                var items = values.TryGetValue(name, out var previous) ? RpadArgument.Items(previous).ToList() : [];
                items.Add(item);
                values[name] = new RpadValue { Items = items.Select(RpadValue.Of).ToList() };
            }
            else values[name] = RpadValue.Of(item);
        }
        return values;
    }

    /// <summary>Stable payload for the runtime process boundary. It contains no secrets beyond the
    /// values already selected for this run.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    public static RpadArgumentContract FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try { return JsonSerializer.Deserialize<RpadArgumentContract>(json) ?? Empty; }
        catch (JsonException) { return Empty; }
    }

    /// <summary>Creates the fixed-width <c>Arguments</c> facade used by the compiler. The replacement name is nine
    /// characters long, matching the old expression width, so source locations remain stable.
    /// The compiler takes the file it goes in (*.Generated.cs) for generated code, where nullable is
    /// off: an annotations context lets an optional argument be T? without warning CS8669.</summary>
    public string GenerateCSharpFacade()
    {
        var builder = new System.Text.StringBuilder("#nullable enable annotations\ninternal static class Arguments\n{\n");
        foreach (var member in Members)
        {
            var type = member.Type == "enum"
                ? "global::" + member.EnumTypeName + (member.IsNullable ? "?" : "")
                : CSharpType(member.Type, member.IsNullable);
            builder.Append("    public static ").Append(type).Append(' ').Append(member.PropertyName).Append(" => ");
            if (member.Type == "enum")
            {
                builder.Append("global::Invoklet.Runtime.ArgumentRuntime.")
                    .Append(member.IsNullable ? "GetEnum" : "GetRequiredEnum").Append("<global::")
                    .Append(member.EnumTypeName).Append(">(\"").Append(member.Name).Append("\")");
            }
            else
                builder.Append('(').Append(type).Append(")global::Invoklet.Runtime.ArgumentRuntime.Get(\"")
                    .Append(member.Name).Append("\")");
            builder.Append(";\n");
        }
        builder.Append("}\n");
        foreach (var member in Members.Where(m => m.Type == "enum"))
        {
            builder.Append("internal enum ").Append(member.EnumTypeName).Append("\n{\n");
            for (var i = 0; i < member.EnumValues.Count; i++)
                builder.Append("    @").Append(RpadArgumentContractMember.EnumMemberName(member.EnumValues[i]))
                    .Append(" = ").Append(i).Append(",\n");
            builder.Append("}\n");
        }
        return builder.Append("#nullable restore\n").ToString();
    }

    public string GenerateVisualBasicFacade()
    {
        var builder = new System.Text.StringBuilder("Friend NotInheritable Class Arguments\n");
        foreach (var member in Members)
        {
            var type = member.Type == "enum"
                ? member.IsNullable ? "Global.System.Nullable(Of " + member.EnumTypeName + ")" : member.EnumTypeName
                : VisualBasicType(member.Type, member.IsNullable);
            builder.Append("    Public Shared ReadOnly Property [").Append(member.PropertyName).Append("] As ")
                .Append(type).Append("\n        Get\n            Return ");
            if (member.Type == "enum")
                builder.Append("Global.Invoklet.Runtime.ArgumentRuntime.")
                    .Append(member.IsNullable ? "GetEnum" : "GetRequiredEnum").Append("(Of ").Append(member.EnumTypeName)
                    .Append(")(\"").Append(member.Name).Append("\")");
            else
                builder.Append("DirectCast(Global.Invoklet.Runtime.ArgumentRuntime.Get(\"")
                    .Append(member.Name).Append("\"), ").Append(type).Append(')');
            builder.Append("\n        End Get\n    End Property\n");
        }
        builder.Append("End Class\n");
        foreach (var member in Members.Where(m => m.Type == "enum"))
        {
            builder.Append("Friend Enum ").Append(member.EnumTypeName).Append('\n');
            for (var i = 0; i < member.EnumValues.Count; i++)
                builder.Append("    [").Append(RpadArgumentContractMember.EnumMemberName(member.EnumValues[i]))
                    .Append("] = ").Append(i).Append('\n');
            builder.Append("End Enum\n");
        }
        return builder.ToString();
    }

    private static string CSharpType(string type, bool nullable = false)
    {
        var result = type switch
        {
            "int" => "global::System.Int32",
            "double" => "global::System.Double",
            "decimal" => "global::System.Decimal",
            "bool" => "global::System.Boolean",
            "datetime" => "global::System.DateTime",
            "timespan" => "global::System.TimeSpan",
            "string[]" => "global::System.String[]",
            _ => "global::System.String",
        };
        return nullable ? result + "?" : result;
    }

    private static string VisualBasicType(string type, bool nullable = false) => type switch
    {
        "int" => nullable ? "Global.System.Nullable(Of Global.System.Int32)" : "Global.System.Int32",
        "double" => nullable ? "Global.System.Nullable(Of Global.System.Double)" : "Global.System.Double",
        "decimal" => nullable ? "Global.System.Nullable(Of Global.System.Decimal)" : "Global.System.Decimal",
        "bool" => nullable ? "Global.System.Nullable(Of Global.System.Boolean)" : "Global.System.Boolean",
        "datetime" => nullable ? "Global.System.Nullable(Of Global.System.DateTime)" : "Global.System.DateTime",
        "timespan" => nullable ? "Global.System.Nullable(Of Global.System.TimeSpan)" : "Global.System.TimeSpan",
        "string[]" => "Global.System.String()",
        _ => "Global.System.String",
    };
}

public sealed record RpadArgumentContractMember(
    string Name,
    string Type,
    IReadOnlyList<string> Values,
    IReadOnlyList<string> EnumValues,
    bool IsNullable = false)
{
    public string PropertyName => Name.Length == 0 ? Name : char.ToUpperInvariant(Name[0]) + Name[1..];
    public string EnumTypeName => "Arguments" + PropertyName;

    /// <summary>Turns a metadata label into a stable identifier shared by C# and Visual Basic.</summary>
    public static string EnumMemberName(string value)
    {
        var builder = new System.Text.StringBuilder();
        var capitalize = true;
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                capitalize = true;
                continue;
            }
            if (builder.Length == 0 && char.IsAsciiDigit(ch)) builder.Append('_');
            builder.Append(capitalize ? char.ToUpperInvariant(ch) : ch);
            capitalize = ch == '_';
        }
        return builder.Length == 0 ? "Value" : builder.ToString();
    }
}
