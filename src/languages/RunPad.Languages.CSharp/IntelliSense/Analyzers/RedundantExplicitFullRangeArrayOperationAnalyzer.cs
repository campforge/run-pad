using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто девяносто третий проход, batch; Reverse form added двести седьмой проход; Copy form
// added двести двадцать первый проход) — own pick, not a Roslynator port. Same "explicit range that
// covers the whole array is redundant" theme as RedundantExplicitFullRangeSortAnalyzer (RPAD0160,
// проход 181), for four more `Array` static methods: `Array.Clear(array, 0, array.Length)`,
// `Array.IndexOf(array, value, 0, array.Length)`, `Array.Reverse(array, 0, array.Length)`, and
// `Array.Copy(src, 0, dst, 0, length)` all spell out zero start indices/a full range explicitly —
// exactly what the shorter overloads (`Array.Clear(array)`/`Array.IndexOf(array,
// value)`/`Array.Reverse(array)`/`Array.Copy(src, dst, length)`) already assume by default. Copy's
// shape is slightly different from the other three — it doesn't require `length` to equal the array's
// own Length (Copy can legitimately copy a prefix), only that BOTH start indices are the literal 0.
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
        description: "Array.Clear/IndexOf/Reverse(array, 0, array.Length) spell out a range that already covers the whole array, and Array.Copy(src, 0, dst, 0, length) spells out zero start indices that its shorter overload already assumes — the shorter overload operates on the same range/indices by default.");

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
            return;
        }

        if (methodName == "Reverse"
            && invocation.ArgumentList.Arguments is [{ Expression: var arrayArg3 }, { Expression: var startArg3 }, { Expression: var lengthArg3 }]
            && IsZeroLiteral(startArg3)
            && lengthArg3 is MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver3 }
            && arrayArg3.IsEquivalentTo(lengthReceiver3)
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "Reverse", IsStatic: true } reverseMethod
            && reverseMethod.ContainingType?.ToDisplayString() == "System.Array")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Array.Reverse(array)"));
            return;
        }

        if (methodName == "Copy"
            && invocation.ArgumentList.Arguments is [_, { Expression: var srcIndexArg }, _, { Expression: var dstIndexArg }, _]
            && IsZeroLiteral(srcIndexArg)
            && IsZeroLiteral(dstIndexArg)
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "Copy", IsStatic: true } copyMethod
            && copyMethod.ContainingType?.ToDisplayString() == "System.Array")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Array.Copy(source, destination, length)"));
        }
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 0 };
}
