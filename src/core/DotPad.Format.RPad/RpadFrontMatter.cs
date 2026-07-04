namespace DotPad.Format.RPad;

public sealed class RpadFrontMatter
{
    public int Version { get; set; } = 1;
    public string Language { get; set; } = "csharp";
    public string TargetFramework { get; set; } = "net10.0";
    public string Kind { get; set; } = "Statements";
    public Dictionary<string, string>? Packages { get; set; }
    public List<string>? References { get; set; }
    public List<string>? Usings { get; set; }
}
