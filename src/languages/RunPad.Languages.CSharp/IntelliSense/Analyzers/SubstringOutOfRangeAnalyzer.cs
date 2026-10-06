using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок первый проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidRandomRangeArgumentAnalyzer (same batch): `"literal".
// Substring(startIndex, length)` throws `ArgumentOutOfRangeException` when `startIndex + length` exceeds
// the string's own length (or either argument is negative) — provable here specifically because the
// RECEIVER is a string literal, so its exact length is known at analysis time, not just the arguments.
// Deliberately restricted to the TWO-argument overload only — the single-argument
// `"literal".Substring(startIndex)` shape was probed alongside this one and dropped: it collides with
// the built-in "Substring can be simplified" (IDE0057) rule, which fires on every single-argument
// Substring call regardless of whether it would throw, so there was nothing left for a second diagnostic
// to add there. IDE0057 does NOT fire on the two-argument overload, leaving this analyzer's territory
// clear.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SubstringOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0235";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Substring(startIndex, length) is out of range for a literal receiver",
        messageFormat: "'\"{0}\".Substring({1}, {2})' always throws ArgumentOutOfRangeException at runtime — the receiver has only {3} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Substring(startIndex, length) throws ArgumentOutOfRangeException when startIndex + length exceeds the string's length, or either argument is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Substring",
                Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } receiverLiteral,
            })
        {
            return;
        }
        if (invocation.ArgumentList.Arguments is not [{ Expression: var startArg }, { Expression: var lengthArg }])
            return;
        if (!TryGetIntLiteralValue(startArg, out var startIndex)) return;
        if (!TryGetIntLiteralValue(lengthArg, out var length)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(receiverLiteral, ct).Value is not string receiverValue)
            return;

        if (startIndex >= 0 && length >= 0 && startIndex + length <= receiverValue.Length) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Substring" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, invocation.GetLocation(), receiverValue, startIndex, length, receiverValue.Length));
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
