namespace DotPad.Contracts;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public interface IDiagnostic
{
    string Code { get; }
    string Message { get; }
    string File { get; }
    int Line { get; }
    int Column { get; }
    DiagnosticSeverity Severity { get; }
}

public interface ICompilationResult
{
    bool Success { get; }
    IReadOnlyList<IDiagnostic> Diagnostics { get; }
}
