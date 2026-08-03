using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят седьмой проход, batch) — own pick, not a Roslynator port. Calling a
// Task/ValueTask-returning method and discarding the result as a bare statement — neither awaited nor
// assigned — silently swallows whatever exception the call might throw and lets the method that called
// it move on before the work is done. The compiler's own CS4014 already warns about exactly this, but
// (verified empirically via a live probe) ONLY inside a method that's itself `async` — a synchronous
// method discarding an async call's Task gets no warning at all, which is the actual gap this fills.
// Deliberately excludes the `_ = M();` discard-assignment form, which is the established idiom for
// "yes, I mean to discard this."
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedAsyncTaskResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0143";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Discarded Task result",
        messageFormat: "The Task returned by this call is discarded — exceptions it throws are silently swallowed; await it, store it, or discard it explicitly with '_ = ...'",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A Task-returning call used as a bare statement is neither awaited nor stored — exceptions from it are lost, and the compiler's CS4014 only warns about this inside async methods.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> AwaitableTypeNames = ImmutableHashSet.Create(
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task<TResult>",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask<TResult>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not InvocationExpressionSyntax invocation) return;

        var ct = context.CancellationToken;
        var returnType = context.SemanticModel.GetTypeInfo(invocation, ct).Type;
        if (returnType?.OriginalDefinition.ToDisplayString() is not { } returnTypeName
            || !AwaitableTypeNames.Contains(returnTypeName))
        {
            return;
        }

        if (IsInsideAsyncFunction(statement)) return; // CS4014 already covers this case

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
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
