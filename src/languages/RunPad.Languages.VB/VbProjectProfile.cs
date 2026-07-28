using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.VB;

// VB's DotnetLanguageProjectProfile (ANALYSIS.md #86) — VB SDK projects have no
// <ImplicitUsings> MSBuild property (that's C#-only); the equivalent "usable without an explicit
// Imports statement" convenience is a project-file <Import> item per default namespace instead.
internal static class VbProjectProfile
{
    // RunPad.Runtime (live user request 2026-07-28, LINQPad Runtime API compatibility) added
    // alongside the BCL set for the exact same reason C#'s own DefaultImportNamespaces now carries
    // it — see CSharpProjectProfile's own comment.
    private static readonly string[] _defaultImports =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks", "RunPad.Runtime",
    ];

    public static readonly DotnetLanguageProjectProfile Instance = new(
        SourceFileExtension: ".vb",
        ProjectFileExtension: ".vbproj",
        ExtraPropertyGroupElements: [],
        DefaultImportNamespaces: _defaultImports,
        ImportItemElementName: "Import",
        FormatImportStatement: ns => $"Imports {ns}");
}
