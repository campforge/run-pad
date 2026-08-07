using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок третий проход, batch) — own pick, not a Roslynator port. Same "min > max
// always throws" theme as InvalidRandomRangeArgumentAnalyzer (RPAD0234, Random.Next(minValue,
// maxValue)): `SortedSet<T>.GetViewBetween(T lowerValue, T upperValue)` throws ArgumentException
// (confirmed empirically, not assumed — NOT ArgumentOutOfRangeException, a different exception type than
// RPAD0234's own family) when `lowerValue` compares greater than `upperValue`. Scoped to two INTEGER
// LITERAL arguments only — `SortedSet<T>` is generic over any `IComparable`, and proving the relative
// order of two arbitrary literals of an arbitrary type would need per-type comparison logic; restricting
// to `int` (resolved via the `SortedSet<int>` element type) keeps this analyzer simple and correct,
// matching the "literal, not arbitrary expression" scoping used throughout this catalog. `lowerValue ==
// upperValue` is valid (returns a single-element view) and correctly excluded.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSortedSetGetViewBetweenArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0309";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SortedSet<T>.GetViewBetween called with a reversed range",
        messageFormat: "'{0}.GetViewBetween({1}, {2})' always throws ArgumentException at runtime — lowerValue must not be greater than upperValue",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SortedSet<T>.GetViewBetween(lowerValue, upperValue) throws ArgumentException when lowerValue compares greater than upperValue.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetViewBetween" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lowerArg }, { Expression: var upperArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(lowerArg, ct).Value is not int lowerValue) return;
        if (context.SemanticModel.GetConstantValue(upperArg, ct).Value is not int upperValue) return;
        if (lowerValue <= upperValue) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetViewBetween" } method)
            return;
        if (method.ContainingType is not { IsGenericType: true } containingType
            || containingType.ConstructedFrom.ToDisplayString() != "System.Collections.Generic.SortedSet<T>")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), lowerValue, upperValue));
    }
}
