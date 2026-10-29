namespace Invoklet.Contracts.IntelliSense;

public enum DiagnosticSeverity { Info, Warning, Error }

public interface IDiagnosticInfo
{
    int StartOffset { get; }
    int EndOffset { get; }
    string Message { get; }
    DiagnosticSeverity Severity { get; }
}
