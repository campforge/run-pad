using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as SubstringOutOfRangeAnalyzer (RPAD0235)/
// InvalidStringRemoveOrInsertArgumentAnalyzer (RPAD0281), applied to the plain indexer
// this time: `"literal"[index]` throws IndexOutOfRangeException when `index` is negative or >= the
// literal's own length — confirmed empirically (not assumed) that the C# compiler does NOT constant-fold
// or bounds-check this itself, unlike an array initializer's fixed-size indexer, so it's a genuine
// runtime-only failure the compiler can't catch on its own. Restricted to a plain literal (or
// unary-minus-literal) `int` index — the `Index`/`Range` indexer overloads (`s[^1]`, `s[1..3]`) are a
// different, more involved shape and are NOT handled here.
// Extended to also handle the `^N` Index-form (`"abc"[^5]`),
// resolved to a plain offset (`receiverValue.Length - N`) and fed through the same bounds check/
// exception type — confirmed empirically to still throw `IndexOutOfRangeException`, same as the plain-int
// form (unlike `List<T>`'s indexer/RPAD0303, which throws `ArgumentOutOfRangeException` for both forms —
// `string`'s indexer, like `Array`'s, is the CLR-special-cased kind). The `Range` form (`s[1..3]`)
// remains out of scope here — genuinely different exception type/mechanism (throws when the SLICE
// doesn't fit, not when a single position is out of bounds), covered instead by the new sibling
// RangeSliceOutOfRangeAnalyzer (RPAD0363).
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

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(receiverLiteral, ct).Value is not string receiverValue)
            return;

        int index;
        object displayIndex;
        if (TryGetIntLiteralValue(indexArg, out index))
        {
            displayIndex = index;
        }
        else if (indexArg is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } fromEndLiteral }
            && fromEndLiteral.Token.Value is int fromEndValue)
        {
            index = receiverValue.Length - fromEndValue;
            displayIndex = $"^{fromEndValue}";
        }
        else
        {
            return;
        }

        if (index >= 0 && index < receiverValue.Length) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true } indexer)
            return;
        if (indexer.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, elementAccess.GetLocation(), receiverValue, displayIndex, receiverValue.Length));
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
