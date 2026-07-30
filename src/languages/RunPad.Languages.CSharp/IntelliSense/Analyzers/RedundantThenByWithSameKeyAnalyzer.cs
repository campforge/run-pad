using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят девятый проход, batch) — own pick, not a Roslynator port.
// `xs.OrderBy(key).ThenBy(key)` sorts by the exact same key twice — after the primary sort, every
// group of elements that compares equal by that key is already as sorted by it as it'll ever be, so a
// secondary sort by the SAME key (ascending or descending, doesn't matter) can never move anything.
// Matched structurally (the two key-selector lambdas must be the literal same expression —
// SyntaxNode.IsEquivalentTo, including parameter names) to keep false-positive risk near zero.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantThenByWithSameKeyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0111";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ThenBy with the same key as the previous sort",
        messageFormat: "'{0}' uses the exact same key as the sort before it — it can never reorder anything, remove it",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A ThenBy/ThenByDescending using the same key as the OrderBy/ThenBy immediately before it has nothing left to sort — every tie by that key was already resolved.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> OrderMethodNames = ImmutableHashSet.Create("OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending");
    private static readonly ImmutableHashSet<string> ThenMethodNames = ImmutableHashSet.Create("ThenBy", "ThenByDescending");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var thenName } memberAccess)
            return;
        if (!ThenMethodNames.Contains(thenName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var thenKeyExpression }]) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments: [{ Expression: var orderKeyExpression }],
            } orderInvocation)
        {
            return;
        }
        if (orderInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var orderName }
            || !OrderMethodNames.Contains(orderName))
        {
            return;
        }
        if (!IsRealEnumerableMethod(context, orderInvocation, ct)) return;

        if (!thenKeyExpression.IsEquivalentTo(orderKeyExpression)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), thenName));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
