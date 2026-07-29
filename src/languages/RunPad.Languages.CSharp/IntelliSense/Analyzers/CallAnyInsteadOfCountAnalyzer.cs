using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок седьмой проход, batch) — idea ported from Roslynator's
// UseAnyMethodInsteadOfCountMethodAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// `xs.Count() > 0` (or `!= 0`/`== 0`) walks the ENTIRE sequence just to compare the total against
// zero — `xs.Any()` stops at the first element. Same LINQ-hygiene theme as the Count/Length-property
// analyzer next to this one, just for the case where there's no O(1) property to fall back on.
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
            SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        // Normalize to (countInvocation, isZeroLiteral) regardless of which side each is on.
        var (countSide, zeroSide, countOnLeft) = IsZeroLiteral(binary.Right)
            ? (binary.Left, binary.Right, true)
            : IsZeroLiteral(binary.Left) ? (binary.Right, binary.Left, false) : (null, null, false);
        if (countSide is null || zeroSide is null) return;

        // `xs.Count() > 0` and `0 < xs.Count()` both mean "has any" — same for `!=`; only `>`/`<`
        // paired with the correct side means "greater than zero" (the other combination, `xs.Count()
        // < 0` or `0 > xs.Count()`, is nonsensical for an unsigned count and not this analyzer's job).
        var kind = binary.Kind();
        var isMeaningful = kind switch
        {
            SyntaxKind.NotEqualsExpression or SyntaxKind.EqualsExpression => true,
            SyntaxKind.GreaterThanExpression => countOnLeft,
            SyntaxKind.LessThanExpression => !countOnLeft,
            _ => false,
        };
        if (!isMeaningful) return;

        if (countSide is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" }) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";
}
