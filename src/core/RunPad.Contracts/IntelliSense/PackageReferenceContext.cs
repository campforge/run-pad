namespace RunPad.Contracts.IntelliSense;

/// <summary>
/// Where a script's NuGet packages are restored when it builds, so IntelliSense can use the same
/// assemblies the compiler does.
/// </summary>
/// <param name="TargetFramework">The script's target framework moniker, e.g. "net10.0".</param>
/// <param name="OutputDirectory">The script's build directory, if it has one; a build restores its
/// packages there.</param>
/// <param name="NuGetConfigFile">The NuGet configuration the build uses, if any.</param>
public sealed record PackageReferenceContext(string TargetFramework, string? OutputDirectory, string? NuGetConfigFile);
