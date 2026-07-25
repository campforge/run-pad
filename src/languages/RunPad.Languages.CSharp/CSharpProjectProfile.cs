using DotPad.Languages.DotnetCommon;

namespace DotPad.Languages.CSharp;

// C#'s DotnetLanguageProjectProfile (ANALYSIS.md #86) — matches the .NET SDK's own
// <ImplicitUsings>enable</ImplicitUsings> set (verified against a real `dotnet build` output —
// obj/<config>/<tfm>/<assembly>.GlobalUsings.g.cs — for a plain Exe project) via the MSBuild property
// itself rather than a hand-maintained namespace list (that's ImplicitUsings.Default's job, for the
// live-analysis side — see its own comment for why the two need to stay in sync).
internal static class CSharpProjectProfile
{
    public static readonly DotnetLanguageProjectProfile Instance = new(
        SourceFileExtension: ".cs",
        ProjectFileExtension: ".csproj",
        ExtraPropertyGroupElements: ["<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"],
        DefaultImportNamespaces: [],
        FormatImportStatement: ns => $"using {ns};");
}
