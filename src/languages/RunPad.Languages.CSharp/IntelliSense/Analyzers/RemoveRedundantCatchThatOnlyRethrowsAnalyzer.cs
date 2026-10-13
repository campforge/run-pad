using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// RemoveRedundantCatchClauseAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator). A
// catch clause whose ENTIRE body is a bare `throw;` — no logging, no cleanup, nothing else — catches
// the exception only to immediately let it continue propagating unchanged. Behaviorally identical to
// not having the catch clause at all (any `finally` on the same try still runs either way).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantCatchThatOnlyRethrowsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0038";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant catch clause that only rethrows",
        messageFormat: "This catch clause only rethrows — it behaves identically to not having a catch clause at all",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A catch clause whose entire body is a bare 'throw;' catches the exception only to immediately let it continue propagating unchanged.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.CatchClause);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var catchClause = (CatchClauseSyntax)context.Node;
        if (catchClause.Filter is not null) return;
        if (catchClause.Block.Statements is not [ThrowStatementSyntax { Expression: null }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation()));
    }
}
