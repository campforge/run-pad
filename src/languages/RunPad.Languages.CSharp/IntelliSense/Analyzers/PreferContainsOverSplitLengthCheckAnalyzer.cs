using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восьмидесятый проход, batch) — own pick, not a Roslynator port.
// `x.Split(sep).Length == 1` allocates a whole array of substrings just to check whether `sep` occurs
// at all — a Length of 1 means the separator was never found (Split with no match returns the original
// string as the only element). `!x.Contains(sep)` asks the same question directly, without building
// the array. Scoped to the single-argument Split overloads (a lone char or string separator) — the
// array-of-separators overloads change what "found" means and aren't this analyzer's concern.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferContainsOverSplitLengthCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0156";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Contains over checking Split().Length against one",
        messageFormat: "Use '{0}' instead of checking 'Split(...).Length' against one — Contains answers the same question without allocating the split array",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Split(sep).Length == 1 means sep was never found — Contains(sep) (or its negation) asks that directly without allocating the array Split returns.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression, SyntaxKind.GreaterThanExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var lengthSide = binary.Kind() switch
        {
            SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression =>
                IsOneLiteral(binary.Right) ? binary.Left : IsOneLiteral(binary.Left) ? binary.Right : null,
            SyntaxKind.GreaterThanExpression when IsOneLiteral(binary.Right) => binary.Left,
            _ => null,
        };
        if (lengthSide is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Length",
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var separatorArg }] } splitInvocation,
            })
        {
            return;
        }
        if (splitInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Split" }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(splitInvocation, ct).Symbol is not IMethodSymbol { Name: "Split" } splitMethod)
            return;
        if (splitMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        var separatorType = context.SemanticModel.GetTypeInfo(separatorArg, ct).Type;
        if (separatorType?.SpecialType is not (SpecialType.System_Char or SpecialType.System_String)) return;

        var suggestion = binary.IsKind(SyntaxKind.EqualsExpression) ? "!Contains(...)" : "Contains(...)";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), suggestion));
    }

    private static bool IsOneLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 1 };
}
