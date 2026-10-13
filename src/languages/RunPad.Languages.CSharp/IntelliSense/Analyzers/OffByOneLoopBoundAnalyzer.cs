using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `for (int i = 0; i <= xs.Count; i++) { ... xs[i] ... }` is the classic off-by-one: on the loop's
// final iteration, i equals xs.Count — one past the last valid index — and indexing xs[i] there throws
// (or, for some collection APIs, silently reads out-of-bounds memory). Deliberately requires BOTH the
// `<=` bound AND an actual `receiver[i]` indexing use inside the loop body with the SAME receiver as
// the bound check (SyntaxNode.IsEquivalentTo) — a `<=` loop that never indexes that same collection
// might be doing something else entirely on purpose, and isn't this analyzer's business.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OffByOneLoopBoundAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0120";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Off-by-one loop bound",
        messageFormat: "Loop condition uses '<=' against '{0}' — the last iteration indexes one past the end (index == {0}); did you mean '<'?",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A for-loop bounded by '<= xs.Count/Length' that also indexes xs[i] inside the body reaches index xs.Count on its last iteration — one past the end.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ForStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var forStatement = (ForStatementSyntax)context.Node;
        if (forStatement.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LessThanOrEqualExpression,
                Left: IdentifierNameSyntax loopVariable,
                Right: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" or "Length" } boundAccess,
            })
        {
            return;
        }

        var loopVariableName = loopVariable.Identifier.Text;
        var declaredByThisLoop = forStatement.Declaration?.Variables.Any(v => v.Identifier.Text == loopVariableName) ?? false;
        if (!declaredByThisLoop) return;

        var indexesTheSameReceiver = forStatement.Statement.DescendantNodesAndSelf()
            .OfType<ElementAccessExpressionSyntax>()
            .Any(elementAccess =>
                elementAccess.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax indexId }]
                && indexId.Identifier.Text == loopVariableName
                && elementAccess.Expression.IsEquivalentTo(boundAccess.Expression));
        if (!indexesTheSameReceiver) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, forStatement.Condition.GetLocation(), boundAccess.Name.Identifier.Text));
    }
}
