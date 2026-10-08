using RunPad.Build.Core.Models;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;

namespace RunPad.Languages.DotnetCommon;

/// <summary>
/// Options shared by every <see cref="CompiledDotnetLanguageHostBase"/> subclass — was two
/// byte-for-byte identical classes (CSharpLanguageHostOptions/VbLanguageHostOptions) before
/// unification (ANALYSIS.md #86). What differs between languages (kind-transform, project-file
/// shape) lives on the host/profile instead, not on the options a caller builds from .rpad front
/// matter.
/// </summary>
public sealed class CompiledDotnetLanguageHostOptions : ILanguageHostOptions
{
    public required DotnetSdk Platform { get; init; }
    public required ExecutionOptions ExecutionOptions { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RuntimeLibPath { get; init; }
    public string OutputName => LanguageHostOptionsDefaults.OutputName;
    public bool BreakOnAllExceptions { get; init; }
    public bool BreakOnUncaughtExceptions { get; init; } = true;

    /// <summary>NuGet package id → version, from the .rpad front matter's <c>packages:</c>.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>();

    /// <summary>Global usings/imports, from the .rpad front matter's <c>namespaces:</c>.</summary>
    public IReadOnlyList<string> Usings { get; init; } = [];

    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];

    /// <summary>
    /// .csproj/.vbproj references from the front matter. Not supported (scripts compile in-process with
    /// Roslyn, no MSBuild): a non-empty list fails the compilation with an explanatory error.
    /// </summary>
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    /// <summary>NuGet.Config the in-process package restore uses (RunPad's own source list); null = the user's NuGet settings.</summary>
    public string? NuGetConfigFile { get; init; }

    public RpadKind Kind { get; init; }

    // TODO.md #111 (bucket 1, Util.CurrentQueryPath) — the .rpad file's own path, null for a
    // never-saved (untitled) tab, same as LINQPad's own Util.CurrentQueryPath for an unsaved query.
    // Threaded down to the compiled script's child process as an environment variable
    // (ChildProcess.ForExecution) for a plain run, and via DapSessionOptions/the DAP launch request's
    // own env for a debug session (netcoredbg spawns the debuggee itself, so
    // ProcessStartInfo.Environment on netcoredbg's own process wouldn't reach it) — either way read
    // back the same way by RunPad.Runtime.Util.CurrentQueryPath.
    public string? CurrentQueryPath { get; init; }
}
