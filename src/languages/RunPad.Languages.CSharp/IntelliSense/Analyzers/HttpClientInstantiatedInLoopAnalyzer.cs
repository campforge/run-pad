using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "allocated fresh every iteration instead of reused" theme as RandomInstantiatedInLoopAnalyzer
// (RPAD0133), for a much more consequential type: `new HttpClient()` inside a loop is one
// of the most well-documented .NET anti-patterns — each instance owns its own socket handler, and even
// when disposed promptly, the underlying TCP connection lingers in TIME_WAIT, so a tight loop can
// exhaust the local port range under load ("socket exhaustion"). HttpClient is explicitly designed to
// be created once and reused (or shared via IHttpClientFactory in DI-based apps) for the lifetime of
// the application. Reuses the loop-detection helper's logic (nearest enclosing loop, excluding a
// for-loop's one-time initializer) rather than sharing code with RPAD0133, matching this codebase's
// existing pattern of small, independent per-analyzer helpers.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HttpClientInstantiatedInLoopAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0164";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HttpClient instantiated inside a loop",
        messageFormat: "'new HttpClient()' inside a loop creates a new socket handler every iteration — a well-known cause of socket exhaustion under load; create one HttpClient and reuse it instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HttpClient is designed to be created once and reused — creating one per loop iteration can exhaust available local ports under load, even when each instance is disposed promptly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(objectCreation, context.CancellationToken).Symbol is not IMethodSymbol
            { MethodKind: MethodKind.Constructor } constructor)
        {
            return;
        }
        if (constructor.ContainingType?.ToDisplayString() != "System.Net.Http.HttpClient") return;

        if (!IsInsideLoopThatRunsEachIteration(objectCreation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, objectCreation.GetLocation()));
    }

    private static bool IsInsideLoopThatRunsEachIteration(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ForStatementSyntax forStatement:
                    var isInOneTimeInit = (forStatement.Declaration is { } decl && decl.Span.Contains(node.Span))
                        || forStatement.Initializers.Any(init => init.Span.Contains(node.Span));
                    return !isInOneTimeInit;
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForEachStatementSyntax:
                    return true;
                case MethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    return false;
            }
        }

        return false;
    }
}
