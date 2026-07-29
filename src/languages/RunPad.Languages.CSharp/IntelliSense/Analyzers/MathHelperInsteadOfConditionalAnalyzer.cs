using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятьдесят второй проход, batch) — idea ported from Roslynator's
// UseCoalesceExpressionAnalyzer-adjacent "prefer helper method" family (Apache License 2.0,
// https://github.com/dotnet/roslynator). `x < 0 ? -x : x`/`x < y ? x : y`/`x > y ? x : y` spell out,
// by hand, exactly what Math.Abs(x)/Math.Min(x, y)/Math.Max(x, y) already do — shorter, and immune to
// getting the comparison direction backwards by accident. Matched purely syntactically (identifiers
// must appear literally identically on both sides) to keep false-positive risk near zero.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MathHelperInsteadOfConditionalAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0044";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use a Math helper method instead of a conditional expression",
        messageFormat: "This conditional expression can be replaced with 'Math.{0}(...)'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "'x < 0 ? -x : x' / 'x < y ? x : y' / 'x > y ? x : y' spell out Math.Abs/Math.Min/Math.Max by hand.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConditionalExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (conditional.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LessThanExpression or (int)SyntaxKind.GreaterThanExpression,
            } condition)
        {
            return;
        }

        var helperName = TryMatchAbs(condition, conditional) ?? TryMatchMinMax(condition, conditional);
        if (helperName is not null)
            context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.GetLocation(), helperName));
    }

    // `x < 0 ? -x : x`
    private static string? TryMatchAbs(BinaryExpressionSyntax condition, ConditionalExpressionSyntax conditional)
    {
        if (!condition.IsKind(SyntaxKind.LessThanExpression)) return null;
        if (condition.Left is not IdentifierNameSyntax x) return null;
        if (!IsZeroLiteral(condition.Right)) return null;

        if (conditional.WhenTrue is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: IdentifierNameSyntax negatedX,
            } || negatedX.Identifier.Text != x.Identifier.Text)
        {
            return null;
        }

        if (conditional.WhenFalse is not IdentifierNameSyntax whenFalseX || whenFalseX.Identifier.Text != x.Identifier.Text)
            return null;

        return "Abs";
    }

    // `x < y ? x : y` (Min) / `x > y ? x : y` (Max)
    private static string? TryMatchMinMax(BinaryExpressionSyntax condition, ConditionalExpressionSyntax conditional)
    {
        if (condition.Left is not IdentifierNameSyntax x || condition.Right is not IdentifierNameSyntax y) return null;
        if (conditional.WhenTrue is not IdentifierNameSyntax whenTrue || whenTrue.Identifier.Text != x.Identifier.Text) return null;
        if (conditional.WhenFalse is not IdentifierNameSyntax whenFalse || whenFalse.Identifier.Text != y.Identifier.Text) return null;

        return condition.IsKind(SyntaxKind.LessThanExpression) ? "Min" : "Max";
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";
}
