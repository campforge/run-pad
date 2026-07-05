namespace DotPad.Format.RPad;

public sealed class RpadFrontMatter
{
    public int Version { get; set; } = 1;
    public RpadLanguage Language { get; set; } = RpadLanguage.CSharp;
    public string TargetSdk { get; set; } = "net10.0";
    public RpadKind Kind { get; set; } = RpadKind.Statements;
    public Dictionary<string, string>? Packages { get; set; }
    public List<string>? References { get; set; }
    public List<string>? Usings { get; set; }
}
