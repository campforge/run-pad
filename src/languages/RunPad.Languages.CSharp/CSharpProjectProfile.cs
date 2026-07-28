using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.CSharp;

// C#'s DotnetLanguageProjectProfile (ANALYSIS.md #86) — matches the .NET SDK's own
// <ImplicitUsings>enable</ImplicitUsings> set (verified against a real `dotnet build` output —
// obj/<config>/<tfm>/<assembly>.GlobalUsings.g.cs — for a plain Exe project) via the MSBuild property
// itself rather than a hand-maintained namespace list (that's ImplicitUsings.Default's job, for the
// live-analysis side — see its own comment for why the two need to stay in sync). RunPad.Runtime
// (Util/.Dump()'s own namespace — live user request 2026-07-28, "make our runtime fully compatible
// with the LINQPad Runtime API") is layered on TOP of that fixed BCL set via DefaultImportNamespaces/
// ImportItemElementName="Using" instead, exactly so a script can reference DumpContainer/
// ICustomMemberProvider/DumpOptions by short name with no using statement of its own — matching how
// LINQPad's own query host implicitly imports the whole LINQPad namespace for every query. (.Dump()
// itself and Util.* already worked with zero imports before this — they're declared in the GLOBAL
// namespace specifically so extension-method/static-member lookup never needed a using at all; this
// is for the handful of ordinary types that aren't extension methods and so don't get that same
// global-namespace trick.)
internal static class CSharpProjectProfile
{
    public static readonly DotnetLanguageProjectProfile Instance = new(
        SourceFileExtension: ".cs",
        ProjectFileExtension: ".csproj",
        ExtraPropertyGroupElements: ["<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"],
        DefaultImportNamespaces: ["RunPad.Runtime"],
        ImportItemElementName: "Using",
        FormatImportStatement: ns => $"using {ns};");
}
