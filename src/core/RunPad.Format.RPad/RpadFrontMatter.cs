namespace DotPad.Format.RPad;

public record TargetSdk(string Name, string? Version = null)
{
    public override string ToString() => $"{Name}{Version}";

    public static readonly TargetSdk Default = new("latest");

    // Splits "net8.0" into Name "net" / Version "8.0"; a value with no digits (e.g. "latest")
    // becomes Name-only.
    public static TargetSdk Parse(string value)
    {
        if (string.IsNullOrEmpty(value)) return Default;
        var idx = 0;
        while (idx < value.Length && !char.IsDigit(value[idx])) idx++;
        return idx == value.Length ? new TargetSdk(value) : new TargetSdk(value[..idx], value[idx..]);
    }
};

public record Package(string Version)
{
    public override string ToString() => $"{Version}";
    public static readonly Package Default = new("latest");
};

public sealed class RpadFrontMatter
{
    public string Version { get; set; } = "1.0";
    public RpadLanguage Language { get; set; } = RpadLanguage.CSharp;
    public TargetSdk TargetSdk { get; set; } = TargetSdk.Default;
    public RpadKind Kind { get; set; } = RpadKind.Statements;
    public Dictionary<string, Package> Packages { get; set; } = new();
    public List<string>? References { get; set; }
    public List<string>? Namespaces { get; set; }
}
