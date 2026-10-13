using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Task.Run(async () => await Foo())` wraps a call to a method that already returns a Task in an
// async lambda whose only job is to immediately await that same call — `Task.Run(Foo)` passes the
// method group directly to the `Func&lt;Task&gt;` overload and does exactly the same thing, without the
// redundant lambda. Deliberately narrow: only the expression-bodied `async () => await Foo()` shape,
// where Foo is a bare, zero-argument, Task-returning method reference — anything more (extra
// statements, arguments, non-Task return) isn't a safe mechanical rewrite.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantAsyncTaskRunWrapperAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0114";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant async lambda wrapper around Task.Run",
        messageFormat: "'Task.Run(async () => await ...)' can be simplified to 'Task.Run(...)' — passing the method group directly avoids the redundant async lambda wrapper",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.Run(async () => await Foo()) wraps Foo (which already returns a Task) in a lambda that does nothing but await it — Task.Run(Foo) does the same thing directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Run" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }, ..]) return;
        if (!IsParameterlessAsyncLambda(lambdaExpression, out var body)) return;

        if (body is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } awaitedCall })
            return;
        if (awaitedCall.Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(awaitedCall, ct).Symbol is not IMethodSymbol calledMethod) return;
        if (calledMethod.Parameters.Length != 0 || !IsTaskReturning(calledMethod.ReturnType)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol runMethod) return;
        if (runMethod.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsParameterlessAsyncLambda(ExpressionSyntax expression, out ExpressionSyntax? body)
    {
        if (expression is ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 0 } lambda
            && lambda.Modifiers.Any(SyntaxKind.AsyncKeyword)
            && lambda.Body is ExpressionSyntax expressionBody)
        {
            body = expressionBody;
            return true;
        }
        body = null;
        return false;
    }

    private static bool IsTaskReturning(ITypeSymbol returnType) =>
        returnType.ToDisplayString() == "System.Threading.Tasks.Task"
        || (returnType is INamedTypeSymbol { IsGenericType: true } named
            && named.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>");
}
