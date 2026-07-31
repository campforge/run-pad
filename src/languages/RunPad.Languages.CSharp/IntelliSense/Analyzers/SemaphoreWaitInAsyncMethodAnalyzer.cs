using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят третий проход, batch) — same "blocking call inside async code" theme as
// ThreadSleepInAsyncMethodAnalyzer (RPAD0089, проход 164). `semaphore.Wait(...)` inside an `async`
// method blocks whatever thread happens to be running the method — usually a pooled thread — until the
// semaphore is available. `await semaphore.WaitAsync(...)` frees the thread for other work while
// waiting instead of parking it, and SemaphoreSlim has a WaitAsync overload matching every Wait one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SemaphoreWaitInAsyncMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0129";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SemaphoreSlim.Wait inside an async method",
        messageFormat: "'SemaphoreSlim.Wait(...)' blocks the current thread inside an async method — use 'await WaitAsync(...)' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SemaphoreSlim.Wait inside async code blocks whatever thread is running the method while waiting, defeating the point of async/await.");

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

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.SemaphoreSlim") return;

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
