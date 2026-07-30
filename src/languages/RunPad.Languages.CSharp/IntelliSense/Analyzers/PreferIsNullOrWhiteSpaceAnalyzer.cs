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
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIsNullOrWhiteSpaceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0084";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer string.IsNullOrWhiteSpace",
        messageFormat: "Use 'string.IsNullOrWhiteSpace(...)' instead of manually trimming and checking for emptiness",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Trim().Length == 0 / Trim() == \"\" reimplement string.IsNullOrWhiteSpace, minus its null-tolerance.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var trimInvocation = ExtractTrimInvocation(binary.Left, binary.Right) ?? ExtractTrimInvocation(binary.Right, binary.Left);
        if (trimInvocation is null) return;

        if (context.SemanticModel.GetSymbolInfo(trimInvocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                Name: "Trim", Parameters.Length: 0, ContainingType.SpecialType: SpecialType.System_String,
            })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

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
