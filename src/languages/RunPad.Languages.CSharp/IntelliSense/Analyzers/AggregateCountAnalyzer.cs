using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двадцать восьмой проход, batch) — own pick, not a Roslynator port. Same
// "hand-rolled fold instead of the dedicated method" theme as AggregateSumAnalyzer (RPAD0085, проход
// 164): `xs.Aggregate(0, (acc, x) => acc + (predicate(x) ? 1 : 0))` hand-rolls exactly what
// `xs.Count(predicate)` already does — tallying how many elements satisfy a predicate through a
// generic fold instead of Count's dedicated implementation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0215";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Count(predicate) instead of Aggregate for a predicate tally",
        messageFormat: "Use 'Count(predicate)' instead of 'Aggregate(0, (acc, x) => acc + (predicate ? 1 : 0))' — Count(predicate) has a dedicated implementation for this",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Aggregate(0, (acc, x) => acc + (predicate(x) ? 1 : 0)) hand-rolls a tally via a generic fold — Count(predicate) does the same counting directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Aggregate" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: var seedExpr },
            {
                Expression: ParenthesizedLambdaExpressionSyntax
                {
                    ParameterList.Parameters: [{ Identifier.Text: var accParamName }, _],
                    Body: BinaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.AddExpression,
                        Left: IdentifierNameSyntax leftId,
                        Right: var rightExpr,
                    },
                },
            },
            ])
        {
            return;
        }
        if (leftId.Identifier.Text != accParamName) return;

        // The ternary must be parenthesized here — `+` binds tighter than `?:`, so
        // `acc + x > 0 ? 1 : 0` would parse as `(acc + x > 0) ? 1 : 0` instead.
        while (rightExpr is ParenthesizedExpressionSyntax parenthesized) rightExpr = parenthesized.Expression;
        if (rightExpr is not ConditionalExpressionSyntax
            {
                WhenTrue: LiteralExpressionSyntax { Token.Value: 1 },
                WhenFalse: LiteralExpressionSyntax { Token.Value: 0 },
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(seedExpr, ct).Value is not 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Aggregate" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
