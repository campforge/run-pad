namespace RunPad.Languages.DotnetCommon;

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
/// <param name="DefaultImportNamespaces">
/// Namespaces every compiled script should see without an explicit import statement of its own — the
/// .NET SDK's C# &lt;ImplicitUsings&gt; only covers a fixed BCL list (ImplicitUsings.Default), so a
/// RunPad-specific namespace like <c>RunPad.Runtime</c> needs this instead, for both C# and VB. Empty
/// for a language with none.
/// </param>
/// <param name="ImportItemElementName">
/// The MSBuild item name <see cref="DefaultImportNamespaces"/> gets emitted as inside the generated
/// project's &lt;ItemGroup&gt; — <c>"Using"</c> for C# (the real, SDK-recognized "global using" item),
/// <c>"Import"</c> for VB (the real "module-level Imports" item; VB has no &lt;ImplicitUsings&gt;
/// property at all, so this is its ONLY mechanism for this). The two are not interchangeable: an
/// &lt;Import Include="X" /&gt; item in a C# project is simply unrecognized (silently has no effect,
/// not an error) — confirmed the hard way, since this field didn't exist yet and
/// DefaultImportNamespaces was C#-empty specifically to avoid emitting the wrong one.
/// </param>
/// <param name="FormatImportStatement">Renders one front-matter namespace as this language's own import statement — e.g. <c>using {ns};</c> vs <c>Imports {ns}</c>.</param>
public sealed record DotnetLanguageProjectProfile(
    string SourceFileExtension,
    string ProjectFileExtension,
    IReadOnlyList<string> ExtraPropertyGroupElements,
    IReadOnlyList<string> DefaultImportNamespaces,
    string ImportItemElementName,
    Func<string, string> FormatImportStatement);
