using System.Runtime.InteropServices;

namespace DotPad.Languages.DotnetCommon;

/// <summary>
/// Expands LINQPad-style path macros in a single .rpad front-matter <c>references:</c> entry
/// (TODO.md #63) — applied identically by both EditorTabItemControl.BuildRunOptions (GUI) and
/// CliRunner.BuildOptions (CLI), which independently build the same
/// CompiledDotnetLanguageHostOptions.AssemblyReferences/ProjectReferences from that same field.
/// </summary>
public static class ReferencePathMacros
{
    /// <param name="reference">A raw references: entry, e.g. "$(MyDocuments)/Shared/Foo.dll".</param>
    /// <param name="currentQueryPath">
    /// The path to the .rpad file currently being run, or null for an untitled tab that's never
    /// been saved. $(CurrentQueryPath) is left unexpanded in that case — there's nothing to
    /// resolve it to — which surfaces as an honest "reference not found" at compile time rather
    /// than silently resolving against some other directory.
    /// </param>
    public static string Expand(string reference, string? currentQueryPath)
    {
        var expanded = reference
            .Replace("$(RuntimeDirectory)", RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Replace("$(MyDocuments)", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        // The directory CONTAINING the script, not the script's own file path — a references:
        // entry names some OTHER file, so "$(CurrentQueryPath)/Lib.dll" only makes sense read as
        // "next to the script I'm running", not "inside the .rpad file itself".
        var currentQueryDirectory = currentQueryPath is null ? null : Path.GetDirectoryName(currentQueryPath);
        if (currentQueryDirectory is not null)
            expanded = expanded.Replace("$(CurrentQueryPath)", currentQueryDirectory);

        return expanded;
    }
}
