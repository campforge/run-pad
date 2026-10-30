using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>The editor's view of a run's request, read as the build reads it (see CompiledDotnetLanguageHostOptions.From).</summary>
public static class DotnetPackageReferenceContext
{
    /// <summary>
    /// Where and on what the script builds, with its references resolved relative to its folder and
    /// macros expanded, and Invoklet's own NuGet.Config. Null while no SDK is known.
    /// </summary>
    public static PackageReferenceContext? For(ExecutionRequest request)
    {
        if (request.Platform?.TargetFrameworkMoniker is not { } targetFramework) return null;
        var references = ReferencePathResolver.Resolve(request.References, request.ReferenceBasePath ?? request.CurrentQueryPath);
        return new PackageReferenceContext(targetFramework, request.OutputDirectory,
            InvokletNuGetConfigStore.ExistingDefaultConfigFile, request.RuntimePath, references.Assemblies);
    }
}
