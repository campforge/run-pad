using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятьдесят первый проход, batch) — idea ported from Roslynator's
// UseStringIsNullOrEmptyMethodAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// `x == null || x == ""` / `x == null || x.Length == 0` say exactly what `string.IsNullOrEmpty(x)`
// already says, more directly and without evaluating `x` twice. Matched purely syntactically (both
// sides must reference the literal same identifier) — deliberately not semantic, since the whole
// point is textual duplication, not type-checking.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseStringIsNullOrEmptyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0043";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use string.IsNullOrEmpty",
        messageFormat: "Use 'string.IsNullOrEmpty({0})' instead of checking for null and empty separately",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "'x == null || x == \"\"' (or '... || x.Length == 0') says exactly what string.IsNullOrEmpty(x) already says, more directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var leftName = GetNullCheckedIdentifierName(binary.Left);
        if (leftName is null) return;

        var rightName = GetEmptyCheckedIdentifierName(binary.Right);
        if (rightName is null || rightName != leftName) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), leftName));
    }

    private static string? GetNullCheckedIdentifierName(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } eq =>
            (eq.Left, eq.Right) switch
            {
                (IdentifierNameSyntax id, var other) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                (var other, IdentifierNameSyntax id) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                _ => null,
            },
        IsPatternExpressionSyntax
        {
            Expression: IdentifierNameSyntax id,
            Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
        } => id.Identifier.Text,
        _ => null,
    };

    private static string? GetEmptyCheckedIdentifierName(ExpressionSyntax expression)
    {
        if (expression is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } eq) return null;

        if (eq.Left is IdentifierNameSyntax leftId && IsEmptyStringLiteral(eq.Right)) return leftId.Identifier.Text;
        if (eq.Right is IdentifierNameSyntax rightId && IsEmptyStringLiteral(eq.Left)) return rightId.Identifier.Text;

        if (eq.Left is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: IdentifierNameSyntax lenLeftId }
            && IsZeroLiteral(eq.Right))
        {
            return lenLeftId.Identifier.Text;
        }
        if (eq.Right is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: IdentifierNameSyntax lenRightId }
            && IsZeroLiteral(eq.Left))
        {
            return lenRightId.Identifier.Text;
        }

        return null;
    }

    private static bool IsEmptyStringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
        && literal.Token.ValueText.Length == 0;

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";
}
