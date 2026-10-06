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
// Extended (сто восемьдесят третий проход, batch) to also cover `Task.Delay(ms).GetAwaiter().GetResult()`
// — the same synchronous-blocking delay, just spelled with the lower-level awaiter API instead of Wait().
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskDelayThenWaitAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0086";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Thread.Sleep instead of Task.Delay(...).Wait()",
        messageFormat: "Use 'Thread.Sleep(...)' instead of '{0}' for a synchronous delay",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.Delay(...).Wait()/Task.Delay(...).GetAwaiter().GetResult() block synchronously anyway — Thread.Sleep(...) does the same thing without the Task/timer machinery.");

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
        var ct = context.CancellationToken;

        InvocationExpressionSyntax? delayInvocation = null;
        var suggestion = "Task.Delay(...).Wait()";

        if (invocation.ArgumentList.Arguments.Count == 0
            && invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Wait", Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } waitReceiver }
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol waitMethod
            && waitMethod.ContainingType?.ToDisplayString() == "System.Threading.Tasks.Task")
        {
            delayInvocation = waitReceiver;
        }
        else if (invocation.ArgumentList.Arguments.Count == 0
            && invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "GetResult",
                Expression: InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAwaiter", Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } getAwaiterReceiver },
                },
            }
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol getResultMethod
            && getResultMethod.ContainingType?.ToDisplayString() == "System.Runtime.CompilerServices.TaskAwaiter")
        {
            delayInvocation = getAwaiterReceiver;
            suggestion = "Task.Delay(...).GetAwaiter().GetResult()";
        }

        if (delayInvocation is null) return;
        if (delayInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Delay" }) return;

        if (context.SemanticModel.GetSymbolInfo(delayInvocation, ct).Symbol is not IMethodSymbol delayMethod) return;
        if (delayMethod.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), suggestion));
    }
}
