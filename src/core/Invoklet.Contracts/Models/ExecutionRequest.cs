using Invoklet.Contracts.Connections;
using Invoklet.Format.RPad;

namespace Invoklet.Contracts.Models;

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

    /// <summary>Serialized selected typed-argument contract for <c>Arguments</c>; null when the
    /// document has no declarations or metadata is invalid.</summary>
    public string? ArgumentContractJson { get; init; }

    /// <summary>Metadata diagnostics that made the typed contract empty.</summary>
    public IReadOnlyList<string> ArgumentContractErrors { get; init; } = [];

    /// <summary>The script's <c>allowUnsafe:</c>: compile with unsafe code allowed, where the language has it.</summary>
    public bool AllowUnsafe { get; init; }

    /// <summary>The code the script's <c>connections:</c> add (see <see cref="GeneratedConnections.For"/>);
    /// null without connections. Made by the caller, which knows the connection drivers.</summary>
    public GeneratedConnections? Connections { get; init; }

    /// <summary>The script's own file; null for one never saved.</summary>
    public string? CurrentQueryPath { get; init; }

    /// <summary>
    /// The file the <c>references:</c> are relative to, when that isn't <see cref="CurrentQueryPath"/>:
    /// an untitled copy of a file keeps resolving them (and <c>$(CurrentQueryPath)</c> in them) from
    /// that file's folder. Only references use it; the script still runs as untitled. Null means
    /// <see cref="CurrentQueryPath"/>.
    /// </summary>
    public string? ReferenceBasePath { get; init; }

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
    /// The request a script's front matter makes: its kind, packages, usings, references, unsafe
    /// setting and arguments, for a script at <paramref name="currentQueryPath"/>. The arguments are
    /// <see cref="RpadArguments"/>'s choice: <paramref name="arguments"/> when given, else the set
    /// <paramref name="argumentSet"/> names, the selected set or declaration defaults. A run checks
    /// <see cref="RpadArguments.Resolve"/>'s error first: with one, the arguments here are empty. A
    /// script without front matter (untitled, never saved) gets the defaults a new document has.
    /// </summary>
    public static ExecutionRequest For(RpadFrontMatter? frontMatter, string? currentQueryPath, IReadOnlyList<string>? arguments = null,
        string? argumentSet = null)
    {
        var contract = RpadArgumentContract.Build(frontMatter, out var contractErrors, arguments, argumentSet);
        return new()
        {
            Kind = frontMatter?.Kind ?? RpadKind.Statements,
            Packages = frontMatter?.Packages.ToDictionary(p => p.Key, p => p.Value.Version) ?? [],
            Usings = frontMatter?.Namespaces ?? [],
            References = frontMatter?.References ?? [],
            Arguments = RpadArguments.Resolve(frontMatter, arguments, argumentSet).Arguments,
            ArgumentContractJson = contractErrors.Count == 0 && contract.Members.Count > 0
                ? contract.ToJson() : null,
            ArgumentContractErrors = contractErrors,
            AllowUnsafe = frontMatter?.AllowUnsafe ?? false,
            CurrentQueryPath = currentQueryPath,
        };
    }
}
