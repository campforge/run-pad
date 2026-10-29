using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.Contracts.Models;

namespace Invoklet.Languages.DotnetCommon.Adapters;

/// <summary>
/// Adapts a raw <see cref="ProcessCompilerResult"/> to <see cref="ICompilationResult"/> — was two
/// identical classes (CSharpCompilationResult/VbCompilationResult) before unification
///.
/// </summary>
public sealed class CompiledDotnetCompilationResult : ICompilationResult
{
    // Exposed internally so CompiledDotnetLanguageHostBase can get the binary path after compilation.
    internal string OutputBinaryFile { get; }

    public bool Success { get; }
    public IReadOnlyList<IDiagnostic> Diagnostics { get; }

    internal CompiledDotnetCompilationResult(ProcessCompilerResult raw)
    {
        Success = raw.Success;
        OutputBinaryFile = raw.OutputBinaryFile;

        var list = new List<IDiagnostic>(raw.Errors.Count + raw.Warnings.Count);
        foreach (var e in raw.Errors)
            list.Add(new CompiledDotnetDiagnostic(e, DiagnosticSeverity.Error));
        foreach (var w in raw.Warnings)
            list.Add(new CompiledDotnetDiagnostic(w, DiagnosticSeverity.Warning));

        Diagnostics = list;
    }
}
