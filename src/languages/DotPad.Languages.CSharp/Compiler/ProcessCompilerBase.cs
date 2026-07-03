using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using Microsoft.CodeAnalysis;

namespace DotPad.Languages.CSharp.Compiler;

public abstract class ProcessCompilerBase : IDotnetProcessCompiler
{
    protected readonly ExecutionPlatform ExecutionPlatform;
    protected readonly string OutputDir;
    protected readonly string DotPadRuntimeLib;
    protected readonly string OutputName;
    protected readonly ExecutionOptions ExecutionOptions;

    protected ProcessCompilerBase(
        ExecutionPlatform executionPlatform,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName)
    {
        ExecutionPlatform = executionPlatform;
        OutputDir = outputDir;
        DotPadRuntimeLib = dotPadRuntimeLib;
        OutputName = outputName;
        ExecutionOptions = executionOptions;
    }

    public abstract Task<ProcessCompilerResult> Compilation(SyntaxTree syntaxTree, CancellationToken cancellationToken);

    public abstract void Dispose();
}
