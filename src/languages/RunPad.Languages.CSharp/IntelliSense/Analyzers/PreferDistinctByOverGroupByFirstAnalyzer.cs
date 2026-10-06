using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят пятый проход, batch) — own pick, not a Roslynator port. A different
// shape than PreferDistinctOverGroupByKeyAnalyzer (RPAD0100, this project — that one covers
// `GroupBy(key).Select(g => g.Key)`, which keeps only the KEYS). This is `GroupBy(key).Select(g =>
// g.First())`, which keeps one full ORIGINAL ELEMENT per key — exactly what `DistinctBy(key)` (.NET
// 6+, well within this project's SDK floor) already does directly, without building groups just to
// take the first member of each and discard the rest.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDistinctByOverGroupByFirstAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0169";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer DistinctBy over GroupBy().Select(g => g.First())",
        messageFormat: "'GroupBy(key).Select(g => g.First())' can be simplified to 'DistinctBy(key)' — avoids building groups just to keep one element from each",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "GroupBy(key).Select(g => g.First()/g.FirstOrDefault()) keeps one original element per key — DistinctBy(key) does the same thing directly, without materializing groups.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" } selectAccess)
            return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: SimpleLambdaExpressionSyntax { Parameter: var groupParam, Body: var selectBody } }])
        {
            return;
        }
        if (selectBody is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "First" or "FirstOrDefault", Expression: IdentifierNameSyntax groupRef },
            })
        {
            return;
        }
        if (groupRef.Identifier.Text != groupParam.Identifier.Text) return;

        if (selectAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GroupBy" },
                ArgumentList.Arguments.Count: 1,
            } groupByInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Select" } selectMethod)
            return;
        if (selectMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(groupByInvocation, ct).Symbol is not IMethodSymbol { Name: "GroupBy" } groupByMethod)
            return;
        if (groupByMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
