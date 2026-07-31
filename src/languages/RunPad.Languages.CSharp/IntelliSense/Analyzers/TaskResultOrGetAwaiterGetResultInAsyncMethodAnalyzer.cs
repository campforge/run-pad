using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят четвёртый проход, batch) — own pick, not a Roslynator port. Same
// "blocking call inside async code" theme as ThreadSleepInAsyncMethodAnalyzer (RPAD0089, проход 164)
// and SemaphoreWaitInAsyncMethodAnalyzer (RPAD0129, проход 173), for the most common sync-over-async
// antipattern: `task.Result`/`task.GetAwaiter().GetResult()` inside an `async` method blocks the
// calling thread until the task completes. In a context with a capturing SynchronizationContext
// (WinForms/WPF UI thread, classic ASP.NET request context), this can deadlock outright if the
// awaited task's own continuation needs that same thread to run — `await task` never has that problem.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0131";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Synchronous task blocking inside an async method",
        messageFormat: "'{0}' blocks the current thread inside an async method and can deadlock in a captured synchronization context — use 'await' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "task.Result/task.GetAwaiter().GetResult() inside async code blocks the calling thread synchronously, which can deadlock when a SynchronizationContext is captured.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> ResultAwaitableTypeNames = ImmutableHashSet.Create(
        "System.Threading.Tasks.Task<TResult>",
        "System.Threading.Tasks.ValueTask<TResult>");

    private static readonly ImmutableHashSet<string> AwaiterTypeNames = ImmutableHashSet.Create(
        "System.Runtime.CompilerServices.TaskAwaiter",
        "System.Runtime.CompilerServices.TaskAwaiter<TResult>",
        "System.Runtime.CompilerServices.ValueTaskAwaiter",
        "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeResultAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeGetAwaiterGetResult, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeResultAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.Text != "Result") return;

        if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol
            { Name: "Result" } property)
        {
            return;
        }
        if (property.ContainingType?.OriginalDefinition.ToDisplayString() is not { } containingTypeName
            || !ResultAwaitableTypeNames.Contains(containingTypeName))
        {
            return;
        }

        if (!IsInsideAsyncFunction(memberAccess)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation(), ".Result"));
    }

    private static void AnalyzeGetAwaiterGetResult(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetResult" } getResultAccess)
            return;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (getResultAccess.Expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAwaiter" },
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetResult" } getResultMethod)
            return;
        if (getResultMethod.ContainingType?.OriginalDefinition.ToDisplayString() is not { } awaiterTypeName
            || !AwaiterTypeNames.Contains(awaiterTypeName))
        {
            return;
        }

        if (!IsInsideAsyncFunction(invocation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "GetAwaiter().GetResult()"));
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
