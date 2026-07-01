using DotPad.Languages.CSharp.Compiler;
using DotPad.Contracts;
using DotPad.Contracts.Models;

namespace DotPad.Languages.CSharp.Adapters;

internal sealed class CSharpCompilationResult : ICompilationResult
{
    // Exposed internally so CSharpLanguageHost can get the binary path after compilation.
    internal string OutputBinaryFile { get; }

    public bool Success { get; }
    public IReadOnlyList<IDiagnostic> Diagnostics { get; }

    internal CSharpCompilationResult(ProcessCompilerResult raw)
    {
        Success = raw.Success;
        OutputBinaryFile = raw.OutputBinaryFile;

        var list = new List<IDiagnostic>(raw.Errors.Count + raw.Warnings.Count);
        foreach (var e in raw.Errors)
            list.Add(new CSharpDiagnostic(e, DiagnosticSeverity.Error));
        foreach (var w in raw.Warnings)
            list.Add(new CSharpDiagnostic(w, DiagnosticSeverity.Warning));

        Diagnostics = list;
    }
}
