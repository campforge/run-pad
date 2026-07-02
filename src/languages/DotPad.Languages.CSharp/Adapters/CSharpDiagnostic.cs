using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Languages.CSharp.Compiler;

namespace DotPad.Languages.CSharp.Adapters;

internal sealed class CSharpDiagnostic : IDiagnostic
{
    private readonly ResultItem _item;

    internal CSharpDiagnostic(ResultItem item, DiagnosticSeverity severity)
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
