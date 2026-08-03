using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят второй проход, batch) — idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). Same "remove empty X block" theme as
// RemoveEmptyFinallyClauseAnalyzer/RemoveEmptyElseClauseAnalyzer/RemoveEmptyDestructorAnalyzer, for
// `lock (x) { }` — an empty lock body still takes and releases the lock (a real, if usually harmless,
// side effect), but does nothing useful while holding it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveEmptyLockStatementAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0076";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty lock statement",
        messageFormat: "Empty 'lock' statement can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A lock statement whose body is empty acquires and releases the lock without doing anything useful while holding it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LockStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var lockStatement = (LockStatementSyntax)context.Node;
        if (lockStatement.Statement is BlockSyntax { Statements.Count: 0 })
            context.ReportDiagnostic(Diagnostic.Create(Rule, lockStatement.LockKeyword.GetLocation()));
    }
}
