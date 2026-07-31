using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто девяносто третий проход, batch) — own pick, not a Roslynator port. Same
// "explicit range that covers the whole array is redundant" theme as
// RedundantExplicitFullRangeSortAnalyzer (RPAD0160, проход 181), for two more `Array` static methods:
// `Array.Clear(array, 0, array.Length)` and `Array.IndexOf(array, value, 0, array.Length)` both spell
// out a start/count range that covers the ENTIRE array — exactly what the shorter overloads
// (`Array.Clear(array)`/`Array.IndexOf(array, value)`) already operate on by default.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantExplicitFullRangeArrayOperationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0181";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit full-range Array operation",
        messageFormat: "'{0}' would do the same thing — the explicit start/count already covers the whole array",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Array.Clear(array, 0, array.Length) and Array.IndexOf(array, value, 0, array.Length) spell out a range that already covers the whole array — the shorter overload operates on the same range by default.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;

        var ct = context.CancellationToken;

        if (methodName == "Clear"
            && invocation.ArgumentList.Arguments is [{ Expression: var arrayArg }, { Expression: var startArg }, { Expression: var lengthArg }]
            && IsZeroLiteral(startArg)
            && lengthArg is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver }
            && arrayArg.IsEquivalentTo(lengthReceiver)
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "Clear", IsStatic: true } clearMethod
            && clearMethod.ContainingType?.ToDisplayString() == "System.Array")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Array.Clear(array)"));
            return;
        }

        if (methodName == "IndexOf"
            && invocation.ArgumentList.Arguments is [{ Expression: var arrayArg2 }, _, { Expression: var startArg2 }, { Expression: var lengthArg2 }]
            && IsZeroLiteral(startArg2)
            && lengthArg2 is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver2 }
            && arrayArg2.IsEquivalentTo(lengthReceiver2)
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "IndexOf", IsStatic: true } indexOfMethod
            && indexOfMethod.ContainingType?.ToDisplayString() == "System.Array")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Array.IndexOf(array, value)"));
        }
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 0 };
}
