using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцатый проход, batch) — own pick, not a Roslynator port. Same
// "concatenating with an empty operand is a no-op" theme as RedundantStringConcatWithEmptyAnalyzer
// (RPAD0169), for LINQ's Concat instead of the `+` operator: `xs.Concat(Enumerable.Empty<T>())` (or the
// mirror, `Enumerable.Empty<T>().Concat(xs)`) wraps xs in an iterator that yields nothing extra — xs
// unwrapped is the same sequence, without the wrapper. Deliberately fires on the WHOLE Concat call, not
// the inner Enumerable.Empty<T>() sub-expression — a separate built-in IDE suggestion nudges that inner
// call toward a collection-expression literal, but this analyzer's fix removes the Concat wrapper (and
// the Empty() call along with it) entirely, so the two don't compete over the same fix.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantConcatWithEmptyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0219";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Remove a Concat with an empty sequence",
        messageFormat: "'{0}' is redundant — concatenating with an empty sequence returns the other sequence unchanged",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "xs.Concat(Enumerable.Empty<T>()) (or Enumerable.Empty<T>().Concat(xs)) always returns a sequence equivalent to xs alone.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Concat", Expression: var receiver })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var argument }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Concat" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (IsEmptyEnumerableCall(context, receiver))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Enumerable.Empty<T>().Concat(xs)"));
        }
        else if (IsEmptyEnumerableCall(context, argument))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "xs.Concat(Enumerable.Empty<T>())"));
        }
    }

    private static bool IsEmptyEnumerableCall(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" },
                ArgumentList.Arguments.Count: 0,
            } invocation)
        {
            return false;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Empty" } method)
            return false;
        return method.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
