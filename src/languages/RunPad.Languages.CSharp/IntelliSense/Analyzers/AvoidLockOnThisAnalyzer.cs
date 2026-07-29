using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок седьмой проход, batch) — idea ported from Roslynator's
// AvoidLockingOnPubliclyAccessibleInstanceAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator), narrowed to its single most unambiguous case: `lock (this)`.
// Any external caller holding a reference to the same object can lock on it too, deadlocking
// unrelated code — the standard fix is a private, dedicated lock object.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AvoidLockOnThisAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0014";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Avoid locking on 'this'",
        messageFormat: "Locking on 'this' can deadlock with unrelated code that also holds a reference to this instance — use a private, dedicated lock object instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Any external caller holding a reference to the same object can lock on it too, causing an unrelated deadlock.");

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
        if (lockStatement.Expression.IsKind(SyntaxKind.ThisExpression))
            context.ReportDiagnostic(Diagnostic.Create(Rule, lockStatement.Expression.GetLocation()));
    }
}
