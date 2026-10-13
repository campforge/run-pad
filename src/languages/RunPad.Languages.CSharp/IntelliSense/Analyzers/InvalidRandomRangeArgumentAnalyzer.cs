using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231)/
// NegativeCountArgumentAnalyzer (RPAD0232)/NegativeTimeoutArgumentAnalyzer (RPAD0233, all проходы
// 239-240): `random.Next(minValue, maxValue)` is documented to throw `ArgumentOutOfRangeException` when
// `minValue > maxValue` — almost certainly a copy-paste mistake with the two arguments swapped. Scoped
// to two INTEGER LITERAL arguments only (same reasoning as the other analyzers in this family — proving
// an arbitrary expression's relative order would need real data-flow analysis); `minValue == maxValue`
// is left alone (that's valid — Next always returns exactly minValue in that case).
// Extended (двести сорок шестой проход, batch) to also cover the single-argument overload —
// `random.Next(maxValue)` throws the same `ArgumentOutOfRangeException` when `maxValue` itself is
// negative (this overload implicitly uses 0 as minValue, and 0 is always greater than a negative
// maxValue).
// Extended again (триста пятнадцатый проход, batch) to also cover `random.NextInt64(minValue,
// maxValue)`/`random.NextInt64(maxValue)` — confirmed empirically to throw identically to `Next` for the
// same shape of arguments (a plain digit literal without an `L` suffix is still parsed by Roslyn as an
// `int`-valued token even though it implicitly converts to `long` at the call site, so the existing
// `TryGetIntLiteralValue` helper works unchanged here).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRandomRangeArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0234";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Random.Next/NextInt64 called with an invalid range",
        messageFormat: "'{0}({1})' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Random.Next(minValue, maxValue)/NextInt64(minValue, maxValue) throw ArgumentOutOfRangeException when minValue is greater than maxValue; the single-argument overloads throw when maxValue itself is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Next" or "NextInt64" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        string argsText;
        string reason;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments is [{ Expression: var minArg }, { Expression: var maxArg }])
        {
            if (!TryGetIntLiteralValue(minArg, out var minValue)) return;
            if (!TryGetIntLiteralValue(maxArg, out var maxValue)) return;
            if (minValue <= maxValue) return;

            argsText = $"{minValue}, {maxValue}";
            reason = $"minValue ({minValue}) is greater than maxValue ({maxValue})";
        }
        else if (arguments is [{ Expression: var maxOnlyArg }])
        {
            if (!TryGetIntLiteralValue(maxOnlyArg, out var maxValue)) return;
            if (maxValue >= 0) return;

            argsText = maxValue.ToString();
            reason = $"maxValue ({maxValue}) cannot be negative";
        }
        else
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Random") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, argsText, reason));
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negativeLiteral })
        {
            if (negativeLiteral.Token.Value is not int positiveValue) return false;
            value = -positiveValue;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal && literal.Token.Value is int literalValue)
        {
            value = literalValue;
            return true;
        }
        return false;
    }
}
