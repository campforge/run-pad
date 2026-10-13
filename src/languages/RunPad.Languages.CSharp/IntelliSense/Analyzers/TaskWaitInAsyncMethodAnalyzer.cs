using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "blocking
// call inside async code" theme as ThreadSleepInAsyncMethodAnalyzer (RPAD0089) and
// SemaphoreWaitInAsyncMethodAnalyzer (RPAD0129), for the plain `Task.Wait()`/`Task.Wait(...)`
// case that neither of those two nor TaskDelayThenWaitAnalyzer (RPAD0086, specific to
// `Task.Delay(...).Wait()`) cover: `task.Wait()` inside an `async` method blocks the calling thread
// until the task completes, exactly the deadlock-in-a-captured-synchronization-context risk that
// TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer (RPAD0131) documents for
// `.Result`/`.GetAwaiter().GetResult()` — `Wait()` has the same risk without producing a value.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskWaitInAsyncMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0147";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Task.Wait() inside an async method",
        messageFormat: "'Task.Wait()' blocks the current thread inside an async method and can deadlock in a captured synchronization context — use 'await' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Task.Wait() inside async code blocks the calling thread synchronously, the same deadlock risk as .Result/.GetAwaiter().GetResult(), just without producing a value.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Wait" }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Wait" } method)
            return;
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() is not ("System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>"))
            return;

        if (!IsInsideAsyncFunction(invocation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsInsideAsyncFunction(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax localFunction:
                    return localFunction.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case SimpleLambdaExpressionSyntax simpleLambda:
                    return simpleLambda.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case ParenthesizedLambdaExpressionSyntax parenLambda:
                    return parenLambda.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case AnonymousMethodExpressionSyntax anonymousMethod:
                    return anonymousMethod.Modifiers.Any(SyntaxKind.AsyncKeyword);
            }
        }

        return false;
    }
}
