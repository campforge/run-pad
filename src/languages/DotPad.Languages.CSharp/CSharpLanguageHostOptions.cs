using DotPad.Build.Core.Models;
using DotPad.Contracts;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHostOptions : ILanguageHostOptions
{
    public required ExecutionPlatform Platform { get; init; }
    public required ExecutionOptions ExecutionOptions { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RuntimeLibPath { get; init; }
    public string OutputName { get; init; } = "DotPadQuery";
}
