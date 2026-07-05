using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using Microsoft.CodeAnalysis;

namespace DotPad.Languages.CSharp.Compiler;

public abstract class ProcessCompilerBase : IDotnetProcessCompiler
{
    protected readonly DotnetSdk Sdk;
    protected readonly string OutputDir;
    protected readonly string DotPadRuntimeLib;
    protected readonly string OutputName;
    protected readonly ExecutionOptions ExecutionOptions;

    protected ProcessCompilerBase(
        DotnetSdk sdk,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName)
    {
        Sdk = sdk;
        OutputDir = outputDir;
        DotPadRuntimeLib = dotPadRuntimeLib;
        OutputName = outputName;
        ExecutionOptions = executionOptions;
    }

    public abstract Task<ProcessCompilerResult> Compilation(string code, CancellationToken cancellationToken);

    public abstract void Dispose();
}
