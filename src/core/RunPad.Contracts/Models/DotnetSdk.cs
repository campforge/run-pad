namespace RunPad.Contracts.Models;

// DoTo: this is specific to a particular language (C#/VB/F#)
public class DotnetSdk
{
    public string Name { get; }
    public string TargetFrameworkMoniker { get; }
    public string Description { get; }
    public string RuntimeConfigVersion { get; }

    public DotnetSdk(string name, string targetFrameworkMoniker, string runtimeConfigVersion, string description)
    {
        Name = name;
        TargetFrameworkMoniker = targetFrameworkMoniker;
        RuntimeConfigVersion = runtimeConfigVersion;
        Description = description;
    }

    public override string ToString() => Description;
}
