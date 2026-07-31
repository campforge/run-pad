using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят первый проход, batch) — own pick, not a Roslynator port.
// `if (cond) return true; else return false;` (and the assignment form, `if (cond) { x = true; } else
// { x = false; }`) only ever produces the condition's own value (or its negation) — `return cond;`/
// `x = cond;` (negated if the branches are flipped) says the same thing directly, without the if/else.
// Probed empirically first: this project's analyzer set doesn't already catch either shape (unlike the
// `x == true`/`x != false` case, which SimplifyBooleanComparisonAnalyzer, RPAD0020, already covers).
// Handles both statement shapes in one analyzer since they're the same underlying idea; the assignment
// shape requires the same target on both branches (checked via IsEquivalentTo).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimplifyIfElseBooleanLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0159";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant if/else choosing between true and false",
        messageFormat: "This if/else only chooses between 'true' and 'false' to {0} — {0} the condition directly (negated if needed) instead",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An if/else whose branches only differ in producing true vs false says nothing the condition itself (or its negation) doesn't already say.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not { Statement: var elseStatementRaw }) return;

        var thenStatement = Unwrap(ifStatement.Statement);
        var elseStatement = Unwrap(elseStatementRaw);

        if (thenStatement is ReturnStatementSyntax { Expression: { } thenReturnExpr }
            && elseStatement is ReturnStatementSyntax { Expression: { } elseReturnExpr }
            && IsBoolLiteralPair(thenReturnExpr, elseReturnExpr))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.GetLocation(), "return"));
            return;
        }

        if (thenStatement is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: var thenTarget,
                    Right: var thenValue,
                },
            }
            && elseStatement is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: var elseTarget,
                    Right: var elseValue,
                },
            }
            && thenTarget.IsEquivalentTo(elseTarget)
            && IsBoolLiteralPair(thenValue, elseValue))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.GetLocation(), "assign"));
        }
    }

    private static StatementSyntax Unwrap(StatementSyntax statement) =>
        statement is BlockSyntax { Statements: [var only] } ? only : statement;

    private static bool IsBoolLiteralPair(ExpressionSyntax a, ExpressionSyntax b) =>
        (a.IsKind(SyntaxKind.TrueLiteralExpression) && b.IsKind(SyntaxKind.FalseLiteralExpression))
        || (a.IsKind(SyntaxKind.FalseLiteralExpression) && b.IsKind(SyntaxKind.TrueLiteralExpression));
}
