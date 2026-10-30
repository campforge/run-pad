using Invoklet.Contracts.Connections;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;

namespace Invoklet.Services;

/// <summary>Builds the plain execution request shared by UI run paths.</summary>
public static class ExecutionRequestBuilder
{
    public static ExecutionRequest Build(
        MdRunFrontMatter? frontMatter,
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
