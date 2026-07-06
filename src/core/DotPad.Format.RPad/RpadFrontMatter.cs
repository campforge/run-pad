namespace DotPad.Format.RPad;

public record TargetSdk(string Name, string? Version = null)
{
    public override string ToString() => $"{Name}{Version}";

    public static readonly TargetSdk Default = new("latest");
};

public record Package(string Version)
{
    public override string ToString() => $"{Version}";
    public static readonly Package Default = new("latest");
};

public sealed class RpadFrontMatter
{
    public int Version { get; set; } = 1;
    public RpadLanguage Language { get; set; } = RpadLanguage.CSharp;
    public TargetSdk TargetSdk { get; set; } = TargetSdk.Default;
    public RpadKind Kind { get; set; } = RpadKind.Statements;
    public Dictionary<string, Package> Packages { get; set; } = new();
    public List<string>? References { get; set; }
    public List<string>? Namespaces { get; set; }
}
