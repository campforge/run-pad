using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.VB;

// VB's DotnetLanguageProjectProfile. VbScriptCompiler turns these DefaultImportNamespaces
// plus the SDK's own default VB imports into project-level imports (vbc /imports:).
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
        DefaultImportNamespaces: _defaultImports,
        FormatImportStatement: ns => $"Imports {ns}");
}
