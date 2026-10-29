using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.Contracts.Connections;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>The tools connection drivers read schemas with, from the NuGet sources scripts restore from.</summary>
public static class DotnetConnectionTools
{
    public static ConnectionTools Default { get; } = new()
    {
        GetPackageFolderAsync = (id, version, ct) =>
            NuGetRestorer.GetPackageFolderAsync(id, version, InvokletNuGetConfigStore.ExistingDefaultConfigFile, ct),
    };
}
