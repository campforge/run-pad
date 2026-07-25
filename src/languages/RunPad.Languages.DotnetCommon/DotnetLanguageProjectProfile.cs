namespace DotPad.Languages.DotnetCommon;

/// <summary>
/// The lexical/project-file conventions that differ between .NET languages compiled the same way
/// (via a throwaway single-file <c>dotnet build</c> project) — everything
/// <see cref="CompiledDotnetLanguageHostBase"/> and <c>MsBuildProcessCompiler</c> need to know about
/// a specific language without duplicating the process/DAP orchestration that's actually identical
/// across languages (ANALYSIS.md #86 — was ~90% duplicated between CSharpLanguageHost/
/// DotnetProcessCompiler and VbLanguageHost/VbProcessCompiler).
/// </summary>
/// <param name="SourceFileExtension">e.g. <c>.cs</c>, <c>.vb</c> — used for the compiled source file and passed to DapSession for breakpoint path matching.</param>
/// <param name="ProjectFileExtension">e.g. <c>.csproj</c>, <c>.vbproj</c>.</param>
/// <param name="ExtraPropertyGroupElements">Language-specific MSBuild properties beyond the shared <c>OutputType</c>/<c>TargetFramework</c>/<c>EmbedAllSources</c> — e.g. C#'s <c>&lt;ImplicitUsings&gt;</c>/<c>&lt;Nullable&gt;</c>. Empty for a language with none.</param>
/// <param name="DefaultImportNamespaces">Namespaces the language's own SDK doesn't make usable without an explicit import (VB has no <c>&lt;ImplicitUsings&gt;</c> MSBuild property, so this is its equivalent convenience via <c>&lt;Import&gt;</c> project items). Empty for a language with none.</param>
/// <param name="FormatImportStatement">Renders one front-matter namespace as this language's own import statement — e.g. <c>using {ns};</c> vs <c>Imports {ns}</c>.</param>
public sealed record DotnetLanguageProjectProfile(
    string SourceFileExtension,
    string ProjectFileExtension,
    IReadOnlyList<string> ExtraPropertyGroupElements,
    IReadOnlyList<string> DefaultImportNamespaces,
    Func<string, string> FormatImportStatement);
