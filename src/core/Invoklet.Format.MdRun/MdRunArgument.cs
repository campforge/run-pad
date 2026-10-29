using System.Globalization;

namespace Invoklet.Format.MdRun;

/// <summary>The type of a declared argument, as <c>type:</c> writes it.</summary>
public enum MdRunArgumentType { String, Int, Bool, Double, Decimal, DateTime, TimeSpan, StringList, Enum }

/// <summary>
/// One argument the script declares (<c>arguments:</c>): its name, type and default value. A run
/// passes it as <c>--name value</c> (a list as <c>--name</c> once per item), with the value of the
/// selected set or else the default; one with neither is left out.
/// </summary>
public sealed class MdRunArgument
{
    public string Name { get; set; } = "";
    public MdRunArgumentType Type { get; set; } = MdRunArgumentType.String;

    /// <summary>The value when the run's set gives none: text, or a list for <see cref="MdRunArgumentType.StringList"/>.</summary>
    public MdRunValue? Default { get; set; }

    /// <summary>An <see cref="MdRunArgumentType.Enum"/>'s values, in order.</summary>
    public List<string> Values { get; set; } = [];

    public MdRunArgument Clone() => new() { Name = Name, Type = Type, Default = Default?.Clone(), Values = [.. Values] };

    public static string TypeName(MdRunArgumentType type) => type switch
    {
        MdRunArgumentType.StringList => "string[]",
        _ => type.ToString().ToLowerInvariant(),
    };

    public static MdRunArgumentType? ParseType(string name) => name switch
    {
        "string" => MdRunArgumentType.String,
        "int" => MdRunArgumentType.Int,
        "bool" => MdRunArgumentType.Bool,
        "double" => MdRunArgumentType.Double,
        "decimal" => MdRunArgumentType.Decimal,
        "datetime" => MdRunArgumentType.DateTime,
        "timespan" => MdRunArgumentType.TimeSpan,
        "string[]" => MdRunArgumentType.StringList,
        "enum" => MdRunArgumentType.Enum,
        _ => null,
    };

    /// <summary>Why <paramref name="value"/> isn't a value of this argument, or null. Numbers, dates and
    /// times are read with the invariant culture (a point, not a comma; dates as 2026-10-26 or
    /// 2026-10-26T09:30:00), so a script reads them the same way on every machine.</summary>
    public string? Check(MdRunValue value)
    {
        if (Type == MdRunArgumentType.StringList)
            return value.Items is { } items && items.All(i => i.Text is not null) || value.Text is not null
                ? null : $"'{Name}' must be a list of text.";
        if (value.Text is not { } text) return $"'{Name}' must be a single value, not a list.";
        var invariant = CultureInfo.InvariantCulture;
        var ok = Type switch
        {
            MdRunArgumentType.String => true,
            MdRunArgumentType.Int => int.TryParse(text, NumberStyles.Integer, invariant, out _),
            MdRunArgumentType.Bool => bool.TryParse(text, out _),
            MdRunArgumentType.Double => double.TryParse(text, NumberStyles.Float, invariant, out _),
            MdRunArgumentType.Decimal => decimal.TryParse(text, NumberStyles.Number, invariant, out _),
            MdRunArgumentType.DateTime => System.DateTime.TryParse(text, invariant, DateTimeStyles.RoundtripKind, out _),
            MdRunArgumentType.TimeSpan => System.TimeSpan.TryParse(text, invariant, out _),
            MdRunArgumentType.Enum => Values.Contains(text, StringComparer.Ordinal),
            _ => false,
        };
        if (ok) return null;
        return Type == MdRunArgumentType.Enum
            ? $"'{Name}' is '{text}', which is not one of {string.Join(", ", Values)}."
            : $"'{Name}' is '{text}', which is not a {TypeName(Type)}.";
    }

    // The items of a value, a single text being a list of one.
    internal static IEnumerable<string> Items(MdRunValue value) =>
        value.Items is { } items ? items.Select(i => i.Text ?? "") : [value.Text ?? ""];
}
