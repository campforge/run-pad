using System.Security;

namespace DotPad.Languages.CSharp.Compiler;

public static class Resources
{
    public static string GetProjectFile(
        string targetFramework,
        string[] assemblyReferences,
        IReadOnlyList<string> projectReferences,
        IReadOnlyDictionary<string, string> nugetPackages)
    {
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
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <EmbedAllSources>true</EmbedAllSources>
                </PropertyGroup>
                <ItemGroup>
            {string.Join("\n", asmRefs)}
            {string.Join("\n", projRefs)}
            {string.Join("\n", pkgRefs)}
                </ItemGroup>
            </Project>
            """;
    }
}
