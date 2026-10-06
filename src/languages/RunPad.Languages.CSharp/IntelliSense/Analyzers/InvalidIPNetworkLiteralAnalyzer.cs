using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста семьдесят третий проход, batch) — own pick, not a Roslynator port. Sibling of
// InvalidIPAddressLiteralAnalyzer (RPAD0249, проход 256) — same "run the real validator" theme, applied
// to the newer (.NET 8+) `System.Net.IPNetwork.Parse(string)` (CIDR notation, e.g. "192.168.1.0/24")
// instead of a plain IP address: throws `FormatException` for a string that isn't a valid
// address-plus-prefix-length pair — confirmed empirically (scratch `dotnet run` script) including the
// easy-to-miss detail that a syntactically valid IP address with the `/prefixLength` suffix simply
// OMITTED (`"192.168.1.0"`, no slash at all) still throws — `IPNetwork` has no bare-address form, the
// slash and prefix are mandatory. Calls the REAL `IPNetwork.TryParse` in-process rather than hand-writing
// CIDR validation logic, same rationale as RPAD0249: no external/environment-dependent data table behind
// the syntax, so the analysis machine and the deployment target can't disagree on what's valid.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIPNetworkLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0366";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IPNetwork.Parse argument is not a valid IP network",
        messageFormat: "'IPNetwork.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid IP network in CIDR notation",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IPNetwork.Parse throws FormatException for a string that isn't a valid IP address plus prefix length in CIDR notation (e.g. \"192.168.1.0/24\").");

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
        if (method.ContainingType?.ToDisplayString() != "System.Net.IPNetwork") return;

        if (IPNetwork.TryParse(value, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
