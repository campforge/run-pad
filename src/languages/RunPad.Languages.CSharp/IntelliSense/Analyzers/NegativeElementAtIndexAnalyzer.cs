using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidRandomRangeArgumentAnalyzer (RPAD0234)/
// SubstringOutOfRangeAnalyzer (RPAD0235, проход 241): `xs.ElementAt(index)` is documented to throw
// `ArgumentOutOfRangeException` when `index` is negative — a mistake the compiler can't catch on its
// own. Scoped to a LITERAL negative integer, same reasoning as the rest of this family.
// Extended in the триста второй проход to also catch a POSITIVE `index` that is out of range for a
// receiver that is itself a freshly created array literal with a statically known element count — same
// "literal receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283, проход 300),
// applied to `.ElementAt(index)` instead of the plain indexer. Confirmed empirically (scratch `dotnet run`
// script) that `ElementAt` throws the same `ArgumentOutOfRangeException` for this case. This coexists with
// (does not collide with) the built-in "Use '[N]' instead of ElementAt()" simplification hint that also
// fires on every array-receiver ElementAt call — that hint's reason (simplify to indexer syntax) is
// unrelated to this analyzer's reason (the call always throws), confirmed via PROBE that both diagnostics
// already coexist on the negative-index case, so the positive-out-of-range case follows the same pattern.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeElementAtIndexAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0236";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ElementAt index always throws ArgumentOutOfRangeException",
        messageFormat: "'xs.ElementAt({0})' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.ElementAt throws ArgumentOutOfRangeException when the index is negative, or when it is out of range for a receiver whose element count is statically known (a freshly created array literal).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ElementAt" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var indexArg }])
            return;

        string reason;
        int value;
        if (TryGetNegativeIntValue(indexArg, out value))
        {
            reason = "index cannot be negative";
        }
        else if (indexArg is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } indexLiteral
            && indexLiteral.Token.Value is int nonNegativeValue && nonNegativeValue >= 0
            && TryGetArrayLiteralElementCount(access.Expression, out var elementCount)
            && nonNegativeValue >= elementCount)
        {
            value = nonNegativeValue;
            reason = $"the receiver has only {elementCount} element(s)";
        }
        else
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ElementAt" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value, reason));
    }

    private static bool TryGetArrayLiteralElementCount(ExpressionSyntax expression, out int elementCount)
    {
        elementCount = 0;
        var initializer = expression switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitArrayCreationExpressionSyntax implicitCreation => implicitCreation.Initializer,
            _ => null,
        };
        if (initializer is null) return false;

        elementCount = initializer.Expressions.Count;
        return true;
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
