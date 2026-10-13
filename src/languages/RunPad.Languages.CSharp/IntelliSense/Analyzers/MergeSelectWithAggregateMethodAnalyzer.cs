using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as
// MergeWhereWithTerminalLinqMethodAnalyzer (RPAD0054, pass 151), for aggregates instead of
// filters/finders. `xs.Select(f).Sum()` (or Average/Min/Max) builds a whole projected intermediate
// sequence just to fold it immediately — each of these aggregate methods has its own selector-
// accepting overload (`xs.Sum(f)`) that does the same projection inline, in a single pass.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MergeSelectWithAggregateMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0060";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Merge Select with the following aggregate method",
        messageFormat: "'Select(...).{0}()' can be simplified to '{0}(...)' — passing the projection directly avoids the intermediate projected sequence",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Select(projection) followed immediately by a selector-accepting aggregate method builds an unnecessary intermediate sequence.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> AggregateMethodNames = ImmutableHashSet.Create("Sum", "Average", "Min", "Max");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var aggregateName } memberAccess)
            return;
        if (!AggregateMethodNames.Contains(aggregateName)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } selectInvocation)
            return;
        if (selectInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" }) return;
        if (!IsRealEnumerableMethod(context, selectInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), aggregateName));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
