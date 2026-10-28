using RunPad.Contracts.Connections;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;

namespace RunPad.Services;

/// <summary>Builds the plain execution request shared by UI run paths.</summary>
public static class ExecutionRequestBuilder
{
    public static ExecutionRequest Build(
        RpadFrontMatter? frontMatter,
        string? filePath,
        GeneratedConnections? connections,
        string? referenceBasePath,
        DotnetSdk? platform,
        string? outputDirectory,
        string? workingDirectory,
        string? runtimePath,
        string? debuggerPath,
        bool breakOnAllExceptions,
        bool breakOnUncaughtExceptions)
        => ExecutionRequest.For(frontMatter, filePath) with
        {
            Connections = connections,
            ReferenceBasePath = referenceBasePath,
            Platform = platform,
            OutputDirectory = outputDirectory,
            WorkingDirectory = workingDirectory,
            RuntimePath = runtimePath,
            DebuggerPath = debuggerPath,
            BreakOnAllExceptions = breakOnAllExceptions,
            BreakOnUncaughtExceptions = breakOnUncaughtExceptions,
        };
}
