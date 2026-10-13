using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). An empty `finally { }` block does nothing — pure leftover
// clutter from an earlier edit (e.g. logging removed but the block left behind).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveEmptyFinallyClauseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty finally clause",
        messageFormat: "Empty 'finally' clause can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An empty finally block does nothing and can be safely removed.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.FinallyClause);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var finallyClause = (FinallyClauseSyntax)context.Node;
        if (finallyClause.Block.Statements.Count == 0)
            context.ReportDiagnostic(Diagnostic.Create(Rule, finallyClause.FinallyKeyword.GetLocation()));
    }
}
