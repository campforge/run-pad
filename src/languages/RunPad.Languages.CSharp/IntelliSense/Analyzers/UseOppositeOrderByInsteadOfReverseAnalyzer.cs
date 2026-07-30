using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятьдесят восьмой проход, batch) — same LINQ-hygiene theme as
// OrderByFollowedByOrderByAnalyzer (RPAD0056, проход 155). `xs.OrderBy(key).Reverse()` sorts
// ascending and then reverses the whole sequence in a second pass — `xs.OrderByDescending(key)` gets
// the same result (assuming a stable sort, which OrderBy/OrderByDescending both are) in one pass and
// reads more directly as intent. Same reasoning applies to OrderBy(...).ThenBy(...) chains — deliberately
// only handles the single-key case, since reversing a multi-key ThenBy chain isn't equivalent to just
// flipping each key to descending in general (it changes tie-breaking order only when combined with
// a prior descending key), so this analyzer bails out on any ThenBy in between.
// Extended (сто пятьдесят девятый проход, batch), and renamed from
// UseOrderByDescendingInsteadOfOrderByThenReverseAnalyzer to match, to also cover the mirrored
// direction — `xs.OrderByDescending(key).Reverse()` → `xs.OrderBy(key)` — same reasoning, same
// diagnostic ID (RPAD0061), just symmetric.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseOppositeOrderByInsteadOfReverseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0061";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the opposite OrderBy variant instead of Reverse",
        messageFormat: "Use '{0}(...)' instead of '{1}(...).Reverse()' — same result in a single pass",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "OrderBy/OrderByDescending immediately followed by Reverse sorts one way and then reverses the whole sequence — the opposite OrderBy variant does both in one pass.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> OppositeOrderByMethod = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, string>("OrderBy", "OrderByDescending"),
        new KeyValuePair<string, string>("OrderByDescending", "OrderBy"),
    ]);

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse" })
            return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } orderByInvocation)
            return;
        if (orderByInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var orderByName }
            || !OppositeOrderByMethod.TryGetValue(orderByName, out var opposite))
        {
            return;
        }
        if (!IsRealEnumerableMethod(context, orderByInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), opposite, orderByName));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
