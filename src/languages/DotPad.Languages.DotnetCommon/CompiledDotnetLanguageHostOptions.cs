using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;

namespace DotPad.Languages.DotnetCommon;

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
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    public RpadKind Kind { get; init; }
}
