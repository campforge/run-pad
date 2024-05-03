namespace DotPad.Build.ProcessCompiler;

public static class Resources
{
    public static string GerProjectFile(string targetFramework, string[] references)
    {
        return @$"
<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>{targetFramework}</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <EmbedAllSources>true</EmbedAllSources>
    </PropertyGroup>
	<ItemGroup>
	{string.Join("\n",references.Select(_ =>
    {
        return
            @$"
		<Reference Include=""{Path.GetFileNameWithoutExtension(_)}"">
			<HintPath>{_}</HintPath>
		</Reference>
		";
    }))}
	</ItemGroup>
</Project>
";
    }
}