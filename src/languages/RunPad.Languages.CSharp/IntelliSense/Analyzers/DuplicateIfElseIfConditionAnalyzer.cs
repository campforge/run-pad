using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UnnecessaryIfElseIfEmptyStatement/duplicate-condition family (Apache License 2.0,
// https://github.com/dotnet/roslynator). In an `if`/`else if` chain, a later branch whose condition
// is structurally identical (SyntaxNode.IsEquivalentTo — exact structural match, not a guess) to an
// EARLIER branch's condition can never be reached: short-circuit evaluation means the earlier branch
// already caught every case the later one would. Almost always a copy-paste mistake (the condition
// was copied but never actually changed for the new branch).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateIfElseIfConditionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0027";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Duplicate condition in if/else-if chain",
        messageFormat: "This condition is identical to an earlier one in the same if/else-if chain — this branch can never be reached",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A later if/else-if branch whose condition exactly matches an earlier one in the same chain is unreachable.");

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
        // Only start from the FIRST `if` of a chain — an `else if` is itself an IfStatement whose
        // parent is an ElseClauseSyntax; walking from there too would re-report the same chain once
        // per link in it.
        if (ifStatement.Parent.IsKind(SyntaxKind.ElseClause)) return;

        var conditions = new List<ExpressionSyntax>();
        var current = ifStatement;
        while (true)
        {
            conditions.Add(current.Condition);
            if (current.Else?.Statement is IfStatementSyntax nextIf) { current = nextIf; continue; }
            break;
        }

        for (var i = 1; i < conditions.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (conditions[i].IsEquivalentTo(conditions[j]))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, conditions[i].GetLocation()));
                    break;
                }
            }
        }
    }
}
