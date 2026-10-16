namespace RunPad.Contracts.IntelliSense;

/// <summary>
/// What a script builds against — its target framework's reference assemblies and the NuGet packages
/// restored for it — so IntelliSense can use the same assemblies the compiler does.
/// </summary>
/// <param name="TargetFramework">The script's target framework moniker, e.g. "net10.0".</param>
/// <param name="OutputDirectory">The script's build directory, if it has one; a build restores its
/// packages there.</param>
/// <param name="NuGetConfigFile">The NuGet configuration the build uses, if any.</param>
/// <param name="DotnetExecutable">The dotnet the build uses (a full path, or a name looked up on PATH),
/// whose install provides the target framework's reference assemblies.</param>
public sealed record PackageReferenceContext(
    string TargetFramework, string? OutputDirectory, string? NuGetConfigFile, string? DotnetExecutable = null);
