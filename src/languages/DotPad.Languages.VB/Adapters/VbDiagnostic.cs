using DotPad.Contracts.Models;
using DotPad.Languages.VB.Compiler;

namespace DotPad.Languages.VB.Adapters;

internal sealed class VbDiagnostic : IDiagnostic
{
    private readonly ResultItem _item;

    internal VbDiagnostic(ResultItem item, DiagnosticSeverity severity)
    {
        _item = item;
        Severity = severity;
    }

    public string Code => _item.Code;
    public string Message => _item.Message;
    public string File => _item.File;
    public int Line => (int)_item.LineSymbol.Line;
    public int Column => (int)_item.LineSymbol.Symbol;
    public DiagnosticSeverity Severity { get; }
}
