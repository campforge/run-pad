using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят четвёртый проход, batch) — own pick, not a Roslynator port.
// `Task.Delay(ms).Wait()` synchronously blocks the calling thread for a delay — which is exactly what
// `Thread.Sleep(ms)` does directly, without allocating a Task and its machinery (timer registration,
// continuation scheduling) just to immediately block on it. If the delay were meant to be awaited
// asynchronously, `Wait()` wouldn't be there — this is specifically the synchronous-blocking case.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskDelayThenWaitAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0086";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Thread.Sleep instead of Task.Delay(...).Wait()",
        messageFormat: "Use 'Thread.Sleep(...)' instead of 'Task.Delay(...).Wait()' for a synchronous delay",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.Delay(...).Wait() blocks synchronously anyway — Thread.Sleep(...) does the same thing without the Task/timer machinery.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Wait" } memberAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol waitMethod) return;
        if (waitMethod.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } delayInvocation)
            return;
        if (delayInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Delay" }) return;

        if (context.SemanticModel.GetSymbolInfo(delayInvocation, ct).Symbol is not IMethodSymbol delayMethod) return;
        if (delayMethod.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
