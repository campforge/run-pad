using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UseAnyMethodInsteadOfCountMethodAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// `xs.Count() > 0` (or `!= 0`/`== 0`) walks the ENTIRE sequence just to compare the total against
// zero — `xs.Any()` stops at the first element. Same LINQ-hygiene theme as the Count/Length-property
// analyzer next to this one, just for the case where there's no O(1) property to fall back on.
// Extended (pass 159, batch) to also cover the predicate-accepting overload —
// `xs.Count(pred) > 0` walks the whole sequence counting matches just to compare against zero;
// `xs.Any(pred)` stops at the first match.
// Extended again (сто шестьдесят третий проход, batch) to also cover the "compare to one" idiom —
// `xs.Count() >= 1`/`xs.Count() < 1` mean exactly the same thing as `xs.Count() > 0`/`== 0`, just
// spelled with a different threshold. Deliberately narrow: only the most common written forms
// (Count() on the left) — the mirrored `1 <= Count()`/`1 > Count()` forms are rare enough in practice
// not to be worth the extra branching.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CallAnyInsteadOfCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0017";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Any() instead of comparing Count() to zero",
        messageFormat: "Use 'Any()' instead of comparing 'Count()' to zero — Any() stops at the first element instead of enumerating everything",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Count() enumerates the whole sequence; Any() short-circuits at the first element.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.GreaterThanExpression, SyntaxKind.LessThanExpression,
            SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression,
            SyntaxKind.GreaterThanOrEqualExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        // `xs.Count() > 0`/`0 < xs.Count()`/`!= 0`/`== 0`/`>= 1`/`< 1` all mean "has any" (or its
        // negation) — the other combinations (`xs.Count() < 0`, `0 > xs.Count()`, etc.) are nonsensical
        // for an unsigned count and not this analyzer's job.
        ExpressionSyntax? countSide = binary.Kind() switch
        {
            SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression =>
                IsZeroLiteral(binary.Right) ? binary.Left : IsZeroLiteral(binary.Left) ? binary.Right : null,
            SyntaxKind.GreaterThanExpression when IsZeroLiteral(binary.Right) => binary.Left, // Count() > 0
            SyntaxKind.LessThanExpression when IsZeroLiteral(binary.Left) => binary.Right, // 0 < Count()
            SyntaxKind.LessThanExpression when IsOneLiteral(binary.Right) => binary.Left, // Count() < 1
            SyntaxKind.GreaterThanOrEqualExpression when IsOneLiteral(binary.Right) => binary.Left, // Count() >= 1
            _ => null,
        };
        if (countSide is null) return;

        if (countSide is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 or 1 } invocation) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" }) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) => IsNumericLiteral(expression, "0");

    private static bool IsOneLiteral(ExpressionSyntax expression) => IsNumericLiteral(expression, "1");

    private static bool IsNumericLiteral(ExpressionSyntax expression, string text) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == text;
}
