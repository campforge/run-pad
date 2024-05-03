using DotPad.Build.ProcessCompiler.Models;
using Microsoft.CodeAnalysis;

namespace DotPad.Build.ProcessCompiler.Interfaces;

public interface IDotnetProcessCompiler : IDisposable
{
    Task<ProcessCompilerResult> Compilation(SyntaxTree syntaxTree, CancellationToken cancellationToken);
}