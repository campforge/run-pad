using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// A real, well-known LINQ gotcha (own pick, not a
// Roslynator port): `xs.OrderBy(a).OrderBy(b)` does NOT sort by a, then by b as a tiebreaker — the
// second OrderBy call starts a brand-new sort from scratch, discarding the first one's ordering
// intent entirely (elements that tie on b keep their relative order from the a-sort only because
// Enumerable.OrderBy happens to be a stable sort, not because the two sorts compose the way the
// author likely expected). `xs.OrderBy(a).ThenBy(b)` is what "sort by a, then by b" actually means.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OrderByFollowedByOrderByAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0056";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "OrderBy followed by OrderBy discards the first sort",
        messageFormat: "This second sort starts over from scratch — use '{0}' instead if you meant to sort by both keys together",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calling OrderBy/OrderByDescending again on an already-sorted sequence re-sorts from scratch instead of adding a secondary sort key — use ThenBy/ThenByDescending for that.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> OrderByMethodNames = ImmutableHashSet.Create("OrderBy", "OrderByDescending");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } memberAccess)
            return;
        if (!OrderByMethodNames.Contains(methodName)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax innerInvocation) return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var innerMethodName }
            || !OrderByMethodNames.Contains(innerMethodName))
        {
            return;
        }
        if (!IsRealEnumerableMethod(context, innerInvocation, ct)) return;

        var suggestion = methodName == "OrderBy" ? "ThenBy" : "ThenByDescending";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), suggestion));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
