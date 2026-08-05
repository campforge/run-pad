using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести девяносто девятый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as SubstringOutOfRangeAnalyzer (RPAD0235)/
// InvalidStringRemoveOrInsertArgumentAnalyzer (RPAD0281, проходы 241/298), applied to the plain indexer
// this time: `"literal"[index]` throws IndexOutOfRangeException when `index` is negative or >= the
// literal's own length — confirmed empirically (not assumed) that the C# compiler does NOT constant-fold
// or bounds-check this itself, unlike an array initializer's fixed-size indexer, so it's a genuine
// runtime-only failure the compiler can't catch on its own. Restricted to a plain literal (or
// unary-minus-literal) `int` index — the `Index`/`Range` indexer overloads (`s[^1]`, `s[1..3]`) are a
// different, more involved shape and are NOT handled here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringLiteralIndexerOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0282";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Indexer is out of range for a literal receiver",
        messageFormat: "'\"{0}\"[{1}]' always throws IndexOutOfRangeException at runtime — the receiver has only {2} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing a string literal with a negative index, or one greater than or equal to its length, throws IndexOutOfRangeException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElementAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } receiverLiteral)
            return;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;
        if (!TryGetIntLiteralValue(indexArg, out var index)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(receiverLiteral, ct).Value is not string receiverValue)
            return;

        if (index >= 0 && index < receiverValue.Length) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true } indexer)
            return;
        if (indexer.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, elementAccess.GetLocation(), receiverValue, index, receiverValue.Length));
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
