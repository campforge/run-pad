using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.GroupBy(key).Select(g => g.Key)` only ever reads the group KEYS — it builds full groups
// (materializing every element into its bucket) just to immediately throw the buckets away.
// `xs.Select(key).Distinct()` gets the same distinct-keys result without ever building a group.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDistinctOverGroupByKeyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0100";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Select().Distinct() over GroupBy().Select(g => g.Key)",
        messageFormat: "'GroupBy(key).Select(g => g.Key)' can be simplified to 'Select(key).Distinct()' — avoids building groups just to discard their contents",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "GroupBy(key).Select(g => g.Key) only reads the group keys — it builds full groups just to throw the buckets away; Select(key).Distinct() gets the same result without ever grouping.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;

        var (paramName, body) = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple =>
                (simple.Parameter.Identifier.Text, exprBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
                (soleParam.Identifier.Text, exprBody),
            _ => (null, null),
        };
        if (paramName is null) return;
        if (body is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Key", Expression: IdentifierNameSyntax keyReceiverId })
            return;
        if (keyReceiverId.Identifier.Text != paramName) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(body, ct).Symbol is not IPropertySymbol { Name: "Key" } keyProperty) return;
        if (keyProperty.ContainingType is not { Name: "IGrouping" } groupingType
            || groupingType.ContainingNamespace?.ToDisplayString() != "System.Linq")
        {
            return;
        }

        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } groupByInvocation)
            return;
        if (groupByInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GroupBy" }) return;
        if (!IsRealEnumerableMethod(context, groupByInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
