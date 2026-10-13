using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231), for
// `Enumerable.Repeat(element, count)`/`Enumerable.Range(start, count)` — both are documented
// to throw `ArgumentOutOfRangeException` when `count` is negative, a mistake the compiler can't catch
// on its own. Scoped to a LITERAL negative integer only, same reasoning as RPAD0231 — an arbitrary
// negative-valued expression would need real data-flow analysis to prove.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeCountArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0232";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative count always throws ArgumentOutOfRangeException",
        messageFormat: "'Enumerable.{0}(..., {1})' always throws ArgumentOutOfRangeException at runtime — count cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.Repeat and Enumerable.Range both throw ArgumentOutOfRangeException when count is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, int> CountParameterIndexByMethod =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("Repeat", 1),
                KeyValuePair.Create("Range", 1),
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!CountParameterIndexByMethod.TryGetValue(methodName, out var countIndex)) return;
        if (invocation.ArgumentList.Arguments.Count <= countIndex) return;

        if (!TryGetNegativeIntValue(invocation.ArgumentList.Arguments[countIndex].Expression, out var value))
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value));
    }

    private static bool TryGetNegativeIntValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }
        if (literal.Token.Value is not int positiveValue) return false;

        value = -positiveValue;
        return true;
    }
}
