using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцать седьмой проход, batch) — own pick, not a Roslynator port. Same
// "lock-acquisition mistake" theme as MonitorEnterWithoutTryFinallyAnalyzer (RPAD0228, same batch), for
// a different mistake: `Monitor.TryEnter(x)` returns false when the lock was NOT acquired (someone else
// already holds it) — calling it as a bare statement discards that result, so code proceeds into what
// it believes is a protected critical section without actually holding the lock at all. Matched at the
// statement level (the invocation is the entire ExpressionStatement, not assigned/checked anywhere) —
// any use of the result (`if (Monitor.TryEnter(x))`, `var acquired = Monitor.TryEnter(x)`) is left
// alone, since the result is genuinely being examined there.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedMonitorTryEnterResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0229";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Monitor.TryEnter result is discarded",
        messageFormat: "'Monitor.TryEnter(...)' returns false when the lock was NOT acquired — discarding the result means code can proceed without actually holding the lock",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Monitor.TryEnter can fail to acquire the lock and return false; calling it as a bare statement ignores that outcome, so the supposedly-protected code may run unsynchronized.");

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
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "TryEnter" },
            } invocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "TryEnter", ReturnType.SpecialType: SpecialType.System_Boolean } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Monitor") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
    }
}
