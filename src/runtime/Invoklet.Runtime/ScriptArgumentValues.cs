using System.Dynamic;
using System.Globalization;
using System.Reflection;

namespace Invoklet.Runtime;

/// <summary>Runtime bridge used by the generated <c>Arguments</c> contract.</summary>
public static class ArgumentRuntime
{
    public static object? Get(string name) => ScriptArgumentValues.Current.Get(name);

    public static TEnum? GetEnum<TEnum>(string name) where TEnum : struct, Enum
    {
        var value = Get(name);
        return value is null ? null : (TEnum)Enum.ToObject(typeof(TEnum), value);
    }

    public static TEnum GetRequiredEnum<TEnum>(string name) where TEnum : struct, Enum
        => GetEnum<TEnum>(name) ?? throw new FormatException($"Argument '{name}' has no enum value.");
}

/// <summary>Runtime view of the selected typed argument contract. The dynamic surface is completed by
/// the editor's generated IntelliSense contract; this object supplies the values to the process.</summary>
internal sealed class ScriptArgumentValues : DynamicObject
{
    private readonly IReadOnlyDictionary<string, object?> _values;
    private static readonly Lazy<ScriptArgumentValues> CurrentValue = new(Load);

    public static ScriptArgumentValues Current => CurrentValue.Value;

    private ScriptArgumentValues(IReadOnlyDictionary<string, object?> values) => _values = values;

    public static ScriptArgumentValues Load()
    {
        var json = Environment.GetEnvironmentVariable("INVOKLET_ARGUMENT_CONTRACT");
        if (string.IsNullOrWhiteSpace(json)) return new(new Dictionary<string, object?>(StringComparer.Ordinal));
        var serializer = Type.GetType("System.Text.Json.JsonSerializer, System.Text.Json", throwOnError: false)
            ?? throw new FormatException("The argument contract requires System.Text.Json.");
        var contractType = typeof(ArgumentContractPayload);
        var optionsType = serializer.Assembly.GetType("System.Text.Json.JsonSerializerOptions", throwOnError: true)!;
        var deserialize = serializer.GetMethod("Deserialize", [typeof(string), typeof(Type), optionsType])
            ?? throw new FormatException("The argument contract serializer is unavailable.");
        ArgumentContractPayload contract;
        try
        {
            contract = (ArgumentContractPayload?)deserialize.Invoke(null, [json, contractType, null])
                ?? throw new FormatException("The argument contract is empty.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new FormatException($"The argument contract could not be read: {ex.InnerException.Message}", ex.InnerException);
        }
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var member in contract.Members ?? [])
        {
            var name = member.Name ?? "";
            var type = member.Type ?? "string";
            var items = member.Values ?? [];
            try
            {
                values[name] = ConvertValue(name, type, items, member.EnumValues ?? []);
            }
            catch (FormatException ex)
            {
                errors.Add(ex.Message);
            }
        }
        if (errors.Count > 0)
            throw new FormatException("Argument values could not be converted:\n" + string.Join("\n", errors));
        return new(values);
    }

    private sealed class ArgumentContractPayload
    {
        public ArgumentContractMemberPayload[]? Members { get; set; }
    }

    private sealed class ArgumentContractMemberPayload
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string[]? Values { get; set; }
        public string[]? EnumValues { get; set; }
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
        => _values.TryGetValue(binder.Name, out result);

    public object? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    private static object ConvertValue(string name, string type, IReadOnlyList<string> values, IReadOnlyList<string> enumValues)
    {
        try
        {
            if (type == "string[]") return values.ToArray();
            if (values.Count == 0) return null!;
            var value = values[0];
            return type switch
            {
                "string" => value,
                "enum" => EnumIndex(value, enumValues),
                "int" => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "double" => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                "decimal" => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                "datetime" => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "timespan" => TimeSpan.Parse(value, CultureInfo.InvariantCulture),
                "bool" => bool.Parse(value),
                _ => throw new FormatException($"unsupported type '{type}'"),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException($"Argument '{name}' value cannot be converted to {type}: {ex.Message}", ex);
        }
    }

    private static int EnumIndex(string value, IReadOnlyList<string> allowed)
    {
        for (var i = 0; i < allowed.Count; i++)
            if (string.Equals(value, allowed[i], StringComparison.Ordinal)) return i;
        throw new FormatException($"'{value}' is not one of: {string.Join(", ", allowed)}.");
    }
}
