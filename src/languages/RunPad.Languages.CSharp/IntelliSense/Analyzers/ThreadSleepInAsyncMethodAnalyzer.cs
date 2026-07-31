using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят четвёртый проход, batch) — own pick, not a Roslynator port. The
// opposite-direction problem from TaskDelayThenWaitAnalyzer (RPAD0086) next to this one:
// `Thread.Sleep(ms)` inside an `async` method blocks whatever thread happens to be running the method
// — usually a pooled thread — for the whole delay, exactly what `async`/`await` exists to avoid.
// `await Task.Delay(ms)` frees the thread for other work during the wait instead of parking it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThreadSleepInAsyncMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0089";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Thread.Sleep inside an async method",
        messageFormat: "'Thread.Sleep(...)' blocks the current thread inside an async method — use 'await Task.Delay(...)' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Thread.Sleep inside async code blocks whatever thread is running the method for the whole delay, defeating the point of async/await.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Sleep" })
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Thread") return;

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
