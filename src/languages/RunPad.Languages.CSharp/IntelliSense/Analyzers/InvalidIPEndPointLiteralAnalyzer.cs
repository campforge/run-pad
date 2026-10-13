using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Third sibling
// in the `System.Net` "run the real validator" Parse family this session, alongside
// InvalidIPAddressLiteralAnalyzer (RPAD0249) and InvalidIPNetworkLiteralAnalyzer (RPAD0366, same проход
// prior): `IPEndPoint.Parse(string)` (address plus port, e.g. "192.168.1.1:80" or "[::1]:80" for IPv6)
// throws `FormatException` for a string that isn't a valid endpoint — confirmed empirically. Kept as a
// SEPARATE analyzer from RPAD0366 rather than folded in, matching this sub-family's own established
// precedent of one analyzer per exact type (RPAD0249/RPAD0366 are already separate from each other
// despite sharing the exact same exception type and "run the real TryParse" mechanism) — also confirmed
// a genuine behavioral difference from RPAD0366 worth keeping distinct in the test suite even if not in
// the exception type: unlike `IPNetwork.Parse`, an `IPEndPoint.Parse` literal with the port suffix simply
// OMITTED (`"192.168.1.1"`, no colon) does NOT throw (defaults to port 0) — confirmed empirically, so
// that specific shape is deliberately excluded from this analyzer's own always-flagged test set.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIPEndPointLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0367";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IPEndPoint.Parse argument is not a valid IP endpoint",
        messageFormat: "'IPEndPoint.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid IP endpoint",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IPEndPoint.Parse throws FormatException for a string that isn't a valid IP address optionally followed by a port (e.g. \"192.168.1.1:80\" or \"[::1]:80\").");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Net.IPEndPoint") return;

        if (IPEndPoint.TryParse(value, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
