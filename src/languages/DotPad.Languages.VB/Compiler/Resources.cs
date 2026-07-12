using System.Security;

namespace DotPad.Languages.VB.Compiler;

public static class Resources
{
    // VB analog of DotPad.Languages.CSharp's ImplicitUsings.Default — VB SDK projects have no
    // <ImplicitUsings> MSBuild property (that's C#-only); the equivalent "usable without an explicit
    // Imports statement" convenience is a project-file <Import> item instead.
    private static readonly string[] _defaultImports =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    public static string GetProjectFile(
        string targetFramework,
        string[] assemblyReferences,
        IReadOnlyList<string> projectReferences,
        IReadOnlyDictionary<string, string> nugetPackages)
    {
        var imports = _defaultImports.Select(ns =>
            $"""
                    <Import Include="{SecurityElement.Escape(ns)}" />
            """);

        var asmRefs = assemblyReferences.Select(r =>
            $"""
                    <Reference Include="{SecurityElement.Escape(Path.GetFileNameWithoutExtension(r))}">
                        <HintPath>{SecurityElement.Escape(r)}</HintPath>
                    </Reference>
            """);

        var projRefs = projectReferences.Select(r =>
            $"""
                    <ProjectReference Include="{SecurityElement.Escape(r)}" />
            """);

        var pkgRefs = nugetPackages.Select(kv =>
            $"""
                    <PackageReference Include="{SecurityElement.Escape(kv.Key)}" Version="{SecurityElement.Escape(kv.Value)}" />
            """);

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>{targetFramework}</TargetFramework>
                    <EmbedAllSources>true</EmbedAllSources>
                </PropertyGroup>
                <ItemGroup>
            {string.Join("\n", imports)}
            {string.Join("\n", asmRefs)}
            {string.Join("\n", projRefs)}
            {string.Join("\n", pkgRefs)}
                </ItemGroup>
            </Project>
            """;
    }
}
