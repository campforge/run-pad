using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `xs.Take(xs.Count())` takes exactly as many elements as the sequence has — a no-op — but pays for it
// with an EXTRA full enumeration of xs (once for Count(), once again for the actual Take/iteration).
// Matched structurally (the receiver of Take and the receiver of Count() must be the literal same
// expression — SyntaxNode.IsEquivalentTo). Always safe to simplify away: if xs were genuinely
// single-pass-only, the original code would already be broken by its own double enumeration.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantTakeCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0108";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Take(Count())",
        messageFormat: "'Take(...Count())' takes exactly as many elements as the sequence has — remove the Take (and the extra enumeration it causes) entirely",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Take(xs.Count()) is a no-op that costs an extra full enumeration of xs just to compute the count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Take" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax countInvocation }])
            return;
        if (countInvocation.ArgumentList.Arguments.Count != 0) return;
        if (countInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" } countAccess)
            return;
        if (!countAccess.Expression.IsEquivalentTo(memberAccess.Expression)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;
        if (!IsRealEnumerableMethod(context, countInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
