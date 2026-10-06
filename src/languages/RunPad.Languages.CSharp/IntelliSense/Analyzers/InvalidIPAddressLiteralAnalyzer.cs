using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести пятьдесят шестой проход, batch) — own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidCultureNameLiteralAnalyzer (RPAD0248, проход 255): `IPAddress.Parse`
// throws `FormatException` for a string that isn't a valid IPv4 or IPv6 address — rather than
// hand-writing an IPv6 regex (notoriously easy to get subtly wrong, given zero-compression, embedded
// IPv4 tails, and scope IDs), this analyzer calls the REAL `System.Net.IPAddress.TryParse` in-process on
// the literal and reports only if THAT call itself returns false — the exact same check the user's own
// `IPAddress.Parse` call would perform at runtime, with the exact same accuracy, and no hand-rolled
// parsing logic to get wrong (TryParse is used instead of Parse+catch purely to avoid an exception path
// inside the analyzer itself; the two share the same underlying parser). Unlike a culture name or an
// encoding name, IP address syntax has no external, environment-dependent data table behind it (it's a
// pure string algorithm), so there's no risk of the analysis machine and the deployment target
// disagreeing on what's valid.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIPAddressLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0249";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IPAddress.Parse argument is not a valid IP address",
        messageFormat: "'IPAddress.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid IPv4 or IPv6 address",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IPAddress.Parse throws FormatException for a string that isn't a valid IPv4 or IPv6 address.");

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
        if (method.ContainingType?.ToDisplayString() != "System.Net.IPAddress") return;

        if (IPAddress.TryParse(value, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
