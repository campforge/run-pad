using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят первый проход, batch) — own pick, not a Roslynator port.
// `Array.Sort(array, 0, array.Length)` and `list.Sort(0, list.Count, comparer)` both spell out a
// start/count range that covers the ENTIRE array/list — exactly what the shorter overload without an
// explicit range already sorts. Checked structurally (the length/count argument must read the same
// receiver's Length/Count property, via IsEquivalentTo) rather than just "any two ints", so a genuinely
// partial range isn't mistaken for a full one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantExplicitFullRangeSortAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0160";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit full-range Sort",
        messageFormat: "'{0}' would sort the same range — the explicit start/count already covers the whole collection",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Array.Sort(array, 0, array.Length) and list.Sort(0, list.Count, comparer) spell out a range that already covers the whole collection — the shorter overload does the same sort.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort" } sortAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Sort" } sortMethod)
            return;

        if (sortMethod.IsStatic
            && sortMethod.ContainingType?.ToDisplayString() == "System.Array"
            && invocation.ArgumentList.Arguments is [{ Expression: var arrayArg }, { Expression: var startArg }, { Expression: var lengthArg }]
            && IsZeroLiteral(startArg)
            && lengthArg is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver }
            && arrayArg.IsEquivalentTo(lengthReceiver))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Array.Sort(array, comparer)"));
            return;
        }

        if (!sortMethod.IsStatic
            && sortMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>"
            && invocation.ArgumentList.Arguments is [{ Expression: var startArg2 }, { Expression: var countArg }, _]
            && IsZeroLiteral(startArg2)
            && countArg is MemberAccessExpressionSyntax { Name.Identifier.Text: "Count", Expression: var countReceiver }
            && sortAccess.Expression.IsEquivalentTo(countReceiver))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "list.Sort(comparer)"));
        }
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 0 };
}
