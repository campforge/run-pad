using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок седьмой проход, batch) — idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). Same rationale as RemoveEmptyFinallyClauseAnalyzer, for
// `else { }`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveEmptyElseClauseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0009";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty else clause",
        messageFormat: "Empty 'else' clause can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An empty else block does nothing and can be safely removed.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElseClause);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elseClause = (ElseClauseSyntax)context.Node;
        // `else if (...)` — the "block" is another IfStatement, not a BlockSyntax — never empty in
        // the sense this analyzer cares about.
        if (elseClause.Statement is BlockSyntax { Statements.Count: 0 })
            context.ReportDiagnostic(Diagnostic.Create(Rule, elseClause.ElseKeyword.GetLocation()));
    }
}
