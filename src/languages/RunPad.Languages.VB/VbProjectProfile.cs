using DotPad.Languages.DotnetCommon;

namespace DotPad.Languages.VB;

// VB's DotnetLanguageProjectProfile (ANALYSIS.md #86) — VB SDK projects have no
// <ImplicitUsings> MSBuild property (that's C#-only); the equivalent "usable without an explicit
// Imports statement" convenience is a project-file <Import> item per default namespace instead.
internal static class VbProjectProfile
{
    private static readonly string[] _defaultImports =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    public static readonly DotnetLanguageProjectProfile Instance = new(
        SourceFileExtension: ".vb",
        ProjectFileExtension: ".vbproj",
        ExtraPropertyGroupElements: [],
        DefaultImportNamespaces: _defaultImports,
        FormatImportStatement: ns => $"Imports {ns}");
}
