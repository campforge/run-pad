namespace DotPad.Languages.CSharp.Compiler;

public static class Resources
{
    // BUG-7: опечатка в имени метода — должно быть GetProjectFile.
    // BUG-4: package ID и version вставляются в XML без эскейпа (SecurityElement.Escape).
    public static string GerProjectFile(
        string targetFramework,
        string[] assemblyReferences,
        IReadOnlyList<string> projectReferences,
        IReadOnlyDictionary<string, string> nugetPackages)
    {
        var asmRefs = assemblyReferences.Select(r =>
            $"""
                    <Reference Include="{Path.GetFileNameWithoutExtension(r)}">
                        <HintPath>{r}</HintPath>
                    </Reference>
            """);

        var projRefs = projectReferences.Select(r =>
            $"""
                    <ProjectReference Include="{r}" />
            """);

        var pkgRefs = nugetPackages.Select(kv =>
            $"""
                    <PackageReference Include="{kv.Key}" Version="{kv.Value}" />
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
