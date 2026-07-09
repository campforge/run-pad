using DotPad.Contracts.Models;
using DotPad.Languages.VB.Compiler;

namespace DotPad.Languages.VB.Adapters;

internal sealed class VbCompilationResult : ICompilationResult
{
    // Exposed internally so VbLanguageHost can get the binary path after compilation.
    internal string OutputBinaryFile { get; }

    public bool Success { get; }
    public IReadOnlyList<IDiagnostic> Diagnostics { get; }

    internal VbCompilationResult(ProcessCompilerResult raw)
    {
        Success = raw.Success;
        OutputBinaryFile = raw.OutputBinaryFile;

        var list = new List<IDiagnostic>(raw.Errors.Count + raw.Warnings.Count);
        foreach (var e in raw.Errors)
            list.Add(new VbDiagnostic(e, DiagnosticSeverity.Error));
        foreach (var w in raw.Warnings)
            list.Add(new VbDiagnostic(w, DiagnosticSeverity.Warning));

        Diagnostics = list;
    }
}
