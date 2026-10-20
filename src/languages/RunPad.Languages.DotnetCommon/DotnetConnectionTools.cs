using RunPad.Contracts.Connections;
using RunPad.Languages.DotnetCommon.Compiler;

namespace RunPad.Languages.DotnetCommon;

/// <summary>The tools connection drivers read schemas with, from the NuGet sources scripts restore from.</summary>
public static class DotnetConnectionTools
{
    public static ConnectionTools Default { get; } = new()
    {
        GetPackageFolderAsync = (id, version, ct) =>
            NuGetRestorer.GetPackageFolderAsync(id, version, RunPadNuGetConfigStore.ExistingDefaultConfigFile, ct),
    };
}
