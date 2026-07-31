using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят четвёртый проход, batch) — own pick, not a Roslynator port. Same
// "recognize the standard library idiom" theme as UseStringIsNullOrEmptyAnalyzer (RPAD0043).
// `s.Trim().Length == 0` and `s.Trim() == ""` are both hand-rolled "is this string blank" checks —
// `string.IsNullOrWhiteSpace(s)` says the same thing directly, and additionally tolerates s being
// null (the hand-rolled forms throw NullReferenceException on a null receiver).
// Extended (сто семидесятый проход, batch) to also cover the negated AND-form —
// `s != null && s.Trim().Length > 0` says exactly what `!string.IsNullOrWhiteSpace(s)` already says.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIsNullOrWhiteSpaceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0084";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer string.IsNullOrWhiteSpace",
        messageFormat: "Use 'string.IsNullOrWhiteSpace(...)' (or its negation) instead of manually trimming and checking for emptiness",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Trim().Length == 0 / Trim() == \"\" reimplement string.IsNullOrWhiteSpace, minus its null-tolerance.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression, SyntaxKind.LogicalAndExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        if (binary.IsKind(SyntaxKind.LogicalAndExpression))
        {
            AnalyzeNotNullAndNotBlank(context, binary);
            return;
        }

        var trimInvocation = ExtractTrimInvocation(binary.Left, binary.Right) ?? ExtractTrimInvocation(binary.Right, binary.Left);
        if (trimInvocation is null) return;

        if (!IsRealTrimMethod(context, trimInvocation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    // s != null && s.Trim().Length > 0  →  !string.IsNullOrWhiteSpace(s)
    private static void AnalyzeNotNullAndNotBlank(SyntaxNodeAnalysisContext context, BinaryExpressionSyntax binary)
    {
        var notNullName = GetNotNullCheckedIdentifierName(binary.Left);
        if (notNullName is null) return;

        if (binary.Right is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.GreaterThanExpression } gt) return;
        if (!IsZeroLiteral(gt.Right)) return;
        if (gt.Left is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Length",
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } trimInvocation,
            })
        {
            return;
        }
        if (trimInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Trim", Expression: IdentifierNameSyntax trimReceiverId }
            || trimReceiverId.Identifier.Text != notNullName)
        {
            return;
        }

        if (!IsRealTrimMethod(context, trimInvocation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsRealTrimMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax trimInvocation) =>
        context.SemanticModel.GetSymbolInfo(trimInvocation, context.CancellationToken).Symbol is IMethodSymbol
        {
            Name: "Trim", Parameters.Length: 0, ContainingType.SpecialType: SpecialType.System_String,
        };

    private static string? GetNotNullCheckedIdentifierName(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } neq =>
            (neq.Left, neq.Right) switch
            {
                (IdentifierNameSyntax id, var other) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                (var other, IdentifierNameSyntax id) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                _ => null,
            },
        IsPatternExpressionSyntax
        {
            Expression: IdentifierNameSyntax id,
            Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } },
        } => id.Identifier.Text, // x is not null
        _ => null,
    };

    private static InvocationExpressionSyntax? ExtractTrimInvocation(ExpressionSyntax side, ExpressionSyntax otherSide)
    {
        // Trim().Length == 0
        if (IsZeroLiteral(otherSide)
            && side is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Length",
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } lengthTrimInvocation,
            }
            && lengthTrimInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Trim" })
        {
            return lengthTrimInvocation;
        }

        // Trim() == ""
        if (IsEmptyStringLiteral(otherSide)
            && side is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } emptyTrimInvocation
            && emptyTrimInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Trim" })
        {
            return emptyTrimInvocation;
        }

        return null;
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";

    private static bool IsEmptyStringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
        && literal.Token.ValueText.Length == 0;
}
