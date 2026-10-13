using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// CombineEnumerableWhereMethodChainAnalyzer, extended to Select as well (Apache License 2.0,
// https://github.com/dotnet/roslynator). `xs.Where(a).Where(b)` and `xs.Select(f).Select(g)` each
// build and iterate an extra intermediate sequence for no reason — `xs.Where(x => a(x) && b(x))`/
// `xs.Select(x => g(f(x)))` do the same work in a single pass.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CombineConsecutiveLinqCallsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0055";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Combine consecutive LINQ calls",
        messageFormat: "Two consecutive '{0}' calls can be combined into one, avoiding an unnecessary intermediate sequence",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Where(a).Where(b) / Select(f).Select(g) each build an extra intermediate sequence that a single combined call would avoid.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> CombinableMethodNames = ImmutableHashSet.Create("Select", "Where");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 1) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } memberAccess)
            return;
        if (!CombinableMethodNames.Contains(methodName)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } innerInvocation)
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var innerMethodName }
            || innerMethodName != methodName)
        {
            return;
        }
        if (!IsRealEnumerableMethod(context, innerInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
