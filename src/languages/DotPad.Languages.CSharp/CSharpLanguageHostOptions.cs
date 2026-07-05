using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.Models;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHostOptions : ILanguageHostOptions
{
    public required ExecutionPlatform Platform { get; init; }
    public required ExecutionOptions ExecutionOptions { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RuntimeLibPath { get; init; }
    public string OutputName { get; init; } = "DotPadQuery";

    public bool BreakOnAllExceptions { get; init; }
    public bool BreakOnUncaughtExceptions { get; init; } = true;
    public bool JustMyCode { get; init; }

    // RPad front-matter overrides (null/empty = use defaults)
    public string Kind { get; init; } = "Statements";
    public string? TargetFrameworkOverride { get; init; }
    public IReadOnlyDictionary<string, string> NugetPackages { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> GlobalUsings { get; init; } = [];
    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
}
