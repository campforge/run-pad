namespace DotPad.Contracts.Models;

public class ExecutionPlatform
{
    public string Name { get; }
    public string TargetFrameworkMoniker { get; }
    public string Description { get; }
    public string RuntimeConfigVersion { get; }

    public ExecutionPlatform(string name, string targetFrameworkMoniker, string runtimeConfigVersion, string description)
    {
        Name = name;
        TargetFrameworkMoniker = targetFrameworkMoniker;
        RuntimeConfigVersion = runtimeConfigVersion;
        Description = description;
    }

    public override string ToString() => Description;
}
