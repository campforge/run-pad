using RunPad.Format.RPad;

namespace RunPad.Contracts.Models;

/// <summary>
/// What a run (and the editor's view of it) asks for, in plain values: the script's metadata as
/// written, where it builds, which runtime and debugger to use and the user's run choices. The
/// language's backend interprets it — resolving references and macros, picking package sources — so
/// that the window and the command-line runner never build a language's own host options.
/// </summary>
public sealed record ExecutionRequest
{
    /// <summary>The SDK to build and run on; null where none is known yet (the editor before one is found).</summary>
    public DotnetSdk? Platform { get; init; }

    public RpadKind Kind { get; init; } = RpadKind.Statements;

    /// <summary>NuGet package id → version, from <c>packages:</c>.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>();

    /// <summary>Global usings/imports, from <c>namespaces:</c>.</summary>
    public IReadOnlyList<string> Usings { get; init; } = [];

    /// <summary>The <c>references:</c> exactly as written: relative to the script's folder, with macros.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The script's <c>args</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The script's own file; null for one never saved.</summary>
    public string? CurrentQueryPath { get; init; }

    /// <summary>Where the script is built (its build-cache folder); null where it has none yet.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Where the script's process starts.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The dotnet host to run with; null for the <c>dotnet</c> on the PATH.</summary>
    public string? RuntimePath { get; init; }

    /// <summary>The debugger to debug with; null or empty for none configured.</summary>
    public string? DebuggerPath { get; init; }

    public bool BreakOnAllExceptions { get; init; }
    public bool BreakOnUncaughtExceptions { get; init; } = true;

    /// <summary>
    /// The request a script's front matter makes: its kind, packages, usings, references and
    /// arguments (unless <paramref name="arguments"/> replaces them), for a script at
    /// <paramref name="currentQueryPath"/>. A script without front matter (untitled, never saved)
    /// gets the defaults a new document has.
    /// </summary>
    public static ExecutionRequest For(RpadFrontMatter? frontMatter, string? currentQueryPath, IReadOnlyList<string>? arguments = null) => new()
    {
        Kind = frontMatter?.Kind ?? RpadKind.Statements,
        Packages = frontMatter?.Packages.ToDictionary(p => p.Key, p => p.Value.Version) ?? [],
        Usings = frontMatter?.Namespaces ?? [],
        References = frontMatter?.References ?? [],
        Arguments = arguments ?? frontMatter?.Args ?? [],
        CurrentQueryPath = currentQueryPath,
    };
}
