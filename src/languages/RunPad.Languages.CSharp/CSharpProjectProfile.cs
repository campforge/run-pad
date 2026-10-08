using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.CSharp;

// C#'s DotnetLanguageProjectProfile (ANALYSIS.md #86). CSharpScriptCompiler adds the .NET SDK's implicit
// usings (ImplicitUsings.Default, shared with IntelliSense) and these DefaultImportNamespaces as
// global usings. RunPad.Runtime (live user request 2026-07-28, LINQPad Runtime API compatibility) lets a
// script name DumpContainer/ICustomMemberProvider/DumpOptions without a using of its own, like
// LINQPad's query host importing the LINQPad namespace. (.Dump() and Util.* are declared in the global
// namespace and never needed one.)
internal static class CSharpProjectProfile
{
    public static readonly DotnetLanguageProjectProfile Instance = new(
        SourceFileExtension: ".cs",
        DefaultImportNamespaces: ["RunPad.Runtime"],
        FormatImportStatement: ns => $"using {ns};");
}
