namespace DotPad.Format.RPad;

public sealed class RpadFrontMatter
{
    public int Version { get; set; } = 1;
    
    // ToDo: сделать enum csharp, povershell... 
    public string Language { get; set; } = "csharp";
    
    // ToDo: переимновать в TargetSdk
    public string TargetFramework { get; set; } = "net10.0";
    
    // сделать enum Statements, Expression, Program
    public string Kind { get; set; } = "Statements";
    public Dictionary<string, string>? Packages { get; set; }
    public List<string>? References { get; set; }
    public List<string>? Usings { get; set; }
}
