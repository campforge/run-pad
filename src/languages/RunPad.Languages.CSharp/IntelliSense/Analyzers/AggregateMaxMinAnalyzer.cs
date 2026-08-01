using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести девятнадцатый проход, batch) — own pick, not a Roslynator port. Same
// "hand-rolled fold mimics a dedicated LINQ method" theme as AggregateSumAnalyzer (RPAD0085, проход
// 164), for Max/Min instead of Sum: `xs.Aggregate((a, b) => Math.Max(a, b))` hand-rolls exactly what
// `xs.Max()` already does through a generic fold. Deliberately restricted to the `Math.Max(a, b)`/
// `Math.Min(a, b)` call shape (not the equivalent ternary `a > b ? a : b`) — that ternary shape is
// already flagged, on its own, by MathHelperInsteadOfConditionalAnalyzer (RPAD0044) regardless of where
// it appears (including inside an Aggregate lambda), so adding a second diagnostic on the enclosing
// Aggregate call for the same underlying reason would just be a second squiggle on the same code.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateMaxMinAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0204";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Max/Min instead of Aggregate for Math.Max/Math.Min",
        messageFormat: "Use '{0}()' instead of 'Aggregate((a, b) => Math.{0}(a, b))' — {0}() has a dedicated implementation for this",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Aggregate((a, b) => Math.Max(a, b)) (or Math.Min) hand-rolls a max/min via a generic fold — Max()/Min() do the same thing directly.");

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
            {
                Expression: ParenthesizedLambdaExpressionSyntax
                {
                    ParameterList.Parameters: [{ Identifier.Text: var paramAName }, { Identifier.Text: var paramBName }],
                    Body: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Max" or "Min" } mathAccess,
                        ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax argA }, { Expression: IdentifierNameSyntax argB }],
                    } mathInvocation,
                },
            },
            ])
        {
            return;
        }
        if (argA.Identifier.Text != paramAName || argB.Identifier.Text != paramBName) return;

        var mathMethodName = mathAccess.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Aggregate" } aggregateMethod)
            return;
        if (aggregateMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(mathInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedMathName } mathMethod
            || resolvedMathName != mathMethodName)
        {
            return;
        }
        if (mathMethod.ContainingType?.ToDisplayString() != "System.Math") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), mathMethodName));
    }
}
