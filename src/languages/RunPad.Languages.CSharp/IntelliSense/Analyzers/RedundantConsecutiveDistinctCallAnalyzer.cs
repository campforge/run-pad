using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Distinct().Distinct()` — Distinct() is idempotent, so the sequence is already fully deduplicated
// after the first call; the second call walks the whole (already-distinct) sequence again for nothing.
// Deliberately restricted to the no-argument overload — Distinct(comparer) with two DIFFERENT
// comparers is not necessarily idempotent-with-itself in the same way, so only the argument-less pair
// is safe to flag.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantConsecutiveDistinctCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0078";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant consecutive Distinct call",
        messageFormat: "'Distinct().Distinct()' is redundant — Distinct() is idempotent, remove the second call",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Distinct() is idempotent — calling it twice in a row re-walks an already-distinct sequence for no benefit.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Distinct" })
            return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } innerInvocation)
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Distinct" }) return;
        if (!IsRealEnumerableMethod(context, innerInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
