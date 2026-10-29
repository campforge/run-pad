using Invoklet.Build.Core.Models;
using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// Options shared by every <see cref="CompiledDotnetLanguageHostBase"/> subclass — was two
/// byte-for-byte identical classes (CSharpLanguageHostOptions/VbLanguageHostOptions) before
/// unification. What differs between languages (kind-transform, project-file
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

    // References that couldn't be resolved (ReferencePathResolver): errors fail the build, warnings
    // (a file that isn't there) are reported with its result.
    public IReadOnlyList<string> ReferenceErrors { get; init; } = [];
    public IReadOnlyList<string> ReferenceWarnings { get; init; } = [];

    /// <summary>NuGet.Config the in-process package restore uses (Invoklet's own source list); null = the user's NuGet settings.</summary>
    public string? NuGetConfigFile { get; init; }

    public RpadKind Kind { get; init; }

    /// <summary>Compile with unsafe code allowed (C# /unsafe); languages without unsafe code ignore it.</summary>
    public bool AllowUnsafe { get; init; }

    // The .rpad file's own path, null for a
    // never-saved (untitled) tab, same as LINQPad's own Util.CurrentQueryPath for an unsaved query.
    // Threaded down to the compiled script's child process as an environment variable
    // (ChildProcess.ForExecution) for a plain run, and via DapSessionOptions/the DAP launch request's
    // own env for a debug session (netcoredbg spawns the debuggee itself, so
    // ProcessStartInfo.Environment on netcoredbg's own process wouldn't reach it) — either way read
    // back the same way by Invoklet.Runtime.Util.CurrentQueryPath.
    public string? CurrentQueryPath { get; init; }

    /// <summary>
    /// The script's command-line arguments (<c>args</c>), resolved from declared arguments or the
    /// <c>Invoklet</c> command line. Passed to the script's process for a run and in the DAP launch
    /// request for a debug session.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Serialized typed <c>Arguments</c> contract for the selected argument set.</summary>
    public string? ArgumentContractJson { get; init; }
    public IReadOnlyList<string> ArgumentContractErrors { get; init; } = [];

    /// <summary>The <c>Connections</c> class the script's connections add, compiled with the script; null without connections.</summary>
    public string? ConnectionSource { get; init; }

    /// <summary>Files the connections' schemas add (a gRPC server's messages and clients), cached as a separate assembly when source sharing is unnecessary.</summary>
    public IReadOnlyList<Contracts.Connections.GeneratedSourceFile> ConnectionFiles { get; init; } = [];

    /// <summary>Why the script's connections can't be built; any of them fails the build.</summary>
    public IReadOnlyList<string> ConnectionErrors { get; init; } = [];

    /// <summary>
    /// The options a run asks for: <paramref name="request"/>'s references resolved relative to the
    /// script's folder with macros expanded (as IntelliSense resolves them), Invoklet's own NuGet.Config
    /// once it exists, and the Invoklet.Runtime library at <paramref name="runtimeLibPath"/>. The same for
    /// every compiled .NET language and for the window and the command-line runner.
    /// </summary>
    public static CompiledDotnetLanguageHostOptions From(ExecutionRequest request, string runtimeLibPath)
    {
        var platform = request.Platform ?? throw new ArgumentException("A run needs an SDK.", nameof(request));
        var outputDirectory = request.OutputDirectory ?? throw new ArgumentException("A run needs an output directory.", nameof(request));
        // Project references aren't supported (scripts compile in-process, no MSBuild): they're still
        // collected so the compiler reports them as an error.
        var references = ReferencePathResolver.Resolve(request.References, request.ReferenceBasePath ?? request.CurrentQueryPath);
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = platform,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = request.WorkingDirectory ?? outputDirectory,
                Runtime = new RuntimeOptions { RuntimePath = request.RuntimePath ?? "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = request.DebuggerPath ?? "" },
            },
            OutputDirectory = outputDirectory,
            RuntimeLibPath = runtimeLibPath,
            BreakOnAllExceptions = request.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = request.BreakOnUncaughtExceptions,
            Kind = request.Kind,
            AllowUnsafe = request.AllowUnsafe,
            Packages = request.Packages,
            ConnectionSource = request.Connections is { Source.Length: > 0 } connections ? connections.Source : null,
            ConnectionErrors = request.Connections?.Errors ?? [],
            ConnectionFiles = request.Connections?.Files ?? [],
            Usings = request.Usings,
            AssemblyReferences = references.Assemblies,
            ProjectReferences = references.Projects,
            ReferenceErrors = references.Errors,
            ReferenceWarnings = references.Warnings,
            CurrentQueryPath = request.CurrentQueryPath,
            Arguments = request.Arguments,
            ArgumentContractJson = request.ArgumentContractJson,
            ArgumentContractErrors = request.ArgumentContractErrors,
            NuGetConfigFile = InvokletNuGetConfigStore.ExistingDefaultConfigFile,
        };
    }
}
