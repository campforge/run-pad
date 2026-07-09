using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;

namespace DotPad.Languages.VB;

public sealed class VbLanguageHostOptions : ILanguageHostOptions
{
    public required DotnetSdk Platform { get; init; }
    public required ExecutionOptions ExecutionOptions { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RuntimeLibPath { get; init; }
    public string OutputName => "RunPadQuery";
    public bool BreakOnAllExceptions { get; init; }
    public bool BreakOnUncaughtExceptions { get; init; } = true;

    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Usings { get; init; } = [];
    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    public RpadKind Kind { get; init; }
}
