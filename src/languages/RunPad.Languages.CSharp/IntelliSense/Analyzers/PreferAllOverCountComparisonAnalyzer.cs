using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семидесятый проход, batch) — own pick, not a Roslynator port.
// `xs.Count(pred) == xs.Count()` means "every element matches pred" — the number of matches equals the
// total count only when nothing was excluded — but says it by fully enumerating xs TWICE (once per
// Count call) and comparing totals. `xs.All(pred)` says the same thing directly, in one pass, with
// early-exit as soon as a non-matching element is found. Matched structurally (both Count receivers
// must be the literal same expression — SyntaxNode.IsEquivalentTo) — deliberately doesn't match
// `xs.Where(pred).Count()` on the predicate side, since that shape is already
// MergeWhereWithTerminalLinqMethodAnalyzer's (RPAD0054) territory; overlapping there would
// double-message the same sub-expression.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferAllOverCountComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0117";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer All over comparing a predicated Count to the total Count",
        messageFormat: "'Count(pred) == Count()' means every element matches — use 'All(pred)' directly instead of enumerating twice",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Count(pred) == Count() checks that every element matches by comparing two full enumerations — All(pred) checks the same thing in one pass with early exit.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!TryMatch(context, binary.Left, binary.Right, out var predicateCall, out var bareCall)
            && !TryMatch(context, binary.Right, binary.Left, out predicateCall, out bareCall))
        {
            return;
        }

        var predicateReceiver = ((MemberAccessExpressionSyntax)predicateCall.Expression).Expression;
        var bareReceiver = ((MemberAccessExpressionSyntax)bareCall.Expression).Expression;
        if (!predicateReceiver.IsEquivalentTo(bareReceiver)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool TryMatch(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax predicateSide,
        ExpressionSyntax bareSide,
        out InvocationExpressionSyntax predicateCall,
        out InvocationExpressionSyntax bareCall)
    {
        predicateCall = null!;
        bareCall = null!;

        if (predicateSide is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" },
                ArgumentList.Arguments.Count: 1,
            } pCall)
        {
            return false;
        }
        if (bareSide is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" },
                ArgumentList.Arguments.Count: 0,
            } bCall)
        {
            return false;
        }

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, pCall, ct) || !IsRealEnumerableMethod(context, bCall, ct)) return false;

        predicateCall = pCall;
        bareCall = bCall;
        return true;
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
