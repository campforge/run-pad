using Invoklet.Contracts.Models;
using Invoklet.Languages.DotnetCommon.Compiler;

namespace Invoklet.Languages.DotnetCommon.Adapters;

internal sealed class CompiledDotnetDiagnostic : IDiagnostic
{
    private readonly ResultItem _item;

    internal CompiledDotnetDiagnostic(ResultItem item, DiagnosticSeverity severity)
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
