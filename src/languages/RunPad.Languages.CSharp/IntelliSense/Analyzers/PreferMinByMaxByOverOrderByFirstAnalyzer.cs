using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятьдесят пятый проход, batch) — same LINQ-hygiene theme as
// UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer/CallAnyInsteadOfCountAnalyzer/
// RemoveRedundantMaterializationBeforeCountAnalyzer (RPAD0015/RPAD0017/RPAD0042). `xs.OrderBy(key).
// First()` sorts the ENTIRE sequence — O(n log n) — just to read the smallest element off the front;
// `xs.MinBy(key)` (.NET 6+, always available given this project's own MinSupportedSdkMajorVersion=6)
// finds it in a single O(n) pass without ever sorting. Same idea for OrderByDescending → MaxBy.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferMinByMaxByOverOrderByFirstAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0053";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use MinBy/MaxBy instead of OrderBy(...).First()",
        messageFormat: "'OrderBy(...).First()' sorts the entire sequence just to read the first element — use '{0}(...)' instead for a single pass",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "OrderBy(key).First()/OrderByDescending(key).First() sort the whole sequence (O(n log n)) just to find one extreme value — MinBy/MaxBy do it in a single O(n) pass.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "First" } memberAccess)
            return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } orderByInvocation)
            return;
        if (orderByInvocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "OrderBy" or "OrderByDescending",
            } orderByAccess)
        {
            return;
        }
        if (!IsRealEnumerableMethod(context, orderByInvocation, ct)) return;

        var suggestion = orderByAccess.Name.Identifier.Text == "OrderBy" ? "MinBy" : "MaxBy";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), suggestion));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
