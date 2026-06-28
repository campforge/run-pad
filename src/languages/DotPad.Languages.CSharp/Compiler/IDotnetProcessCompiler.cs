using Microsoft.CodeAnalysis;

namespace DotPad.Languages.CSharp.Compiler;

public interface IDotnetProcessCompiler : IDisposable
{
    Task<ProcessCompilerResult> Compilation(SyntaxTree syntaxTree, CancellationToken cancellationToken);
}
