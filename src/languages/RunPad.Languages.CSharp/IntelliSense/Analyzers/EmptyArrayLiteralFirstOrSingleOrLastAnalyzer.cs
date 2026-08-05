using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста семнадцатый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283, проход 300), applied
// to the LINQ terminal operators `.First()`/`.Single()`/`.Last()` (with or without a predicate) instead
// of the plain indexer: calling any of them on a freshly created array literal with ZERO elements always
// throws InvalidOperationException, regardless of whether a predicate is supplied (there is nothing to
// test the predicate against either way) — confirmed empirically via scratch `dotnet run` script, not
// assumed. Deliberately scoped to First/Single/Last only — `.Max()`/`.Min()` have nullable-friendly
// overloads that return `null` instead of throwing for an empty sequence when the element type is a
// nullable value type, so their throw/no-throw outcome depends on the element type in a way First/
// Single/Last's does not, making them a separate, more involved case not attempted in this batch. This
// coexists with (does not collide with) the built-in "Use '[0]'/'[^1]' instead of First()/Last()"
// simplification hint that also fires on array-receiver calls — that hint's reason (simplify to indexer
// syntax) is unrelated to this analyzer's reason (the call always throws), same coexistence precedent as
// NegativeElementAtIndexAnalyzer's positive-range extension (RPAD0236, проход 302).
// Extended in the триста восемнадцатый проход to also cover the single-argument (no-seed)
// `.Aggregate(func)` overload — confirmed empirically to throw InvalidOperationException for an empty
// receiver with no element-type nuance (unlike Max/Min, still out of scope). The two-argument
// `Aggregate(seed, func)` overload does NOT throw for an empty sequence (it just returns `seed`), but
// needs no special-casing here — it's already excluded by the existing `Arguments.Count > 1` guard.
// Class name/`DiagnosticId` kept as-is despite the scope growing beyond First/Single/Last, same
// precedent as other same-mechanism extensions in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyArrayLiteralFirstOrSingleOrLastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0290";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "First/Single/Last/Aggregate on an empty array literal always throws",
        messageFormat: "'xs.{0}(...)' always throws InvalidOperationException at runtime — the receiver array literal has no elements",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.First/Single/Last (with or without a predicate) and the no-seed Aggregate(func) throw InvalidOperationException when the source sequence has no elements, provable here because the receiver is a freshly created array literal with a known, empty element count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "First" or "Single" or "Last" or "Aggregate" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments.Count > 1) return;

        var ct = context.CancellationToken;
        if (TryGetArrayLiteralElementCount(access.Expression, context.SemanticModel, ct) is not { } elementCount
            || elementCount != 0)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }

    private static int? TryGetArrayLiteralElementCount(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        if (expression is ArrayCreationExpressionSyntax arrayCreation)
        {
            if (arrayCreation.Initializer is { } init) return init.Expressions.Count;
            if (arrayCreation.Type.RankSpecifiers is [{ Sizes: [var sizeExpr] }, ..]
                && semanticModel.GetConstantValue(sizeExpr, ct).Value is int size)
            {
                return size;
            }
            return null;
        }
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return implicitInit.Expressions.Count;
        return null;
    }
}
