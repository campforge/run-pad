using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Monitor.Enter(x)` acquires the lock manually — the `lock` statement exists specifically so nobody
// has to hand-write the try/finally that guarantees `Monitor.Exit(x)` still runs if the protected code
// throws. `Monitor.Enter(x)` NOT immediately followed by a `try` statement with a `finally` clause means
// an exception between the two calls leaves the lock held forever, deadlocking every other thread that
// tries to acquire it. Deliberately narrow: only checks that the VERY NEXT statement in the same block
// is a try/finally — doesn't attempt to verify the finally actually calls Monitor.Exit(x) with the same
// target (that would need matching across an arbitrary finally body, more guesswork than this analyzer
// is willing to do); still catches the most common form of the mistake (no try/finally at all).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MonitorEnterWithoutTryFinallyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0228";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Monitor.Enter not immediately followed by try/finally",
        messageFormat: "'Monitor.Enter(...)' should be immediately followed by a try/finally that calls 'Monitor.Exit(...)' — otherwise an exception before the matching Exit leaves the lock held forever",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Without a try/finally guaranteeing Monitor.Exit runs, an exception thrown after Monitor.Enter permanently deadlocks every other thread waiting on the same lock.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Enter" },
            } invocation)
        {
            return;
        }
        if (statement.Parent is not BlockSyntax { Statements: var statements }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Enter" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Monitor") return;

        var index = statements.IndexOf(statement);
        if (index < 0) return;

        if (index + 1 < statements.Count && statements[index + 1] is TryStatementSyntax { Finally: not null })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
    }
}
