namespace RunPad.Languages.DotnetCommon;

/// <summary>
/// The lexical conventions that differ between .NET languages compiled the same way (in-process
/// Roslyn, <see cref="Compiler.RoslynScriptCompiler"/>) — what <see cref="CompiledDotnetLanguageHostBase"/>
/// and the language's compiler need to know about a specific language without duplicating the
/// process/DAP orchestration that's identical across languages (ANALYSIS.md #86).
/// </summary>
/// <param name="SourceFileExtension">e.g. <c>.cs</c>, <c>.vb</c> — used for the compiled source file and passed to DapSession for breakpoint path matching.</param>
/// <param name="DefaultImportNamespaces">
/// Namespaces every compiled script sees without an import statement of its own, on top of the
/// language's SDK defaults (C#'s implicit usings, VB's default imports) — e.g. <c>RunPad.Runtime</c>.
/// </param>
/// <param name="FormatImportStatement">Renders one front-matter namespace as this language's own import statement — e.g. <c>using {ns};</c> vs <c>Imports {ns}</c>.</param>
public sealed record DotnetLanguageProjectProfile(
    string SourceFileExtension,
    IReadOnlyList<string> DefaultImportNamespaces,
    Func<string, string> FormatImportStatement);
