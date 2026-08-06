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
// Extended in the триста сороковой проход to also cover `.Average()`/`.Min()`/`.Max()`/`.MinBy(...)`/
// `.MaxBy(...)` — the case explicitly deferred above. Confirmed empirically (scratch `dotnet run`
// script) that the type-dependence is precisely characterized by the RESOLVED method's RETURN type: when
// it's a non-nullable value type (the specialized `int`/`long`/`float`/`double`/`decimal` overloads, or
// `TSource`/`TResult` instantiated with a non-nullable value type), the call always throws
// `InvalidOperationException` on an empty source; when the return type is `Nullable<T>` or a reference
// type, it returns `default`/`null` instead — confirmed for EVERY combination tried, including
// `Average`'s selector overload (returns non-nullable `double` even when the SOURCE element type is a
// reference type like `string`, and still throws), and `Min`/`Max`/`MinBy`/`MaxBy`'s `IComparer`-taking
// overloads (still throw — supplying a comparer doesn't change the empty-sequence behavior). This single
// return-type check subsumes all the argument-count variants (0-arg Min/Max, 1-arg Min/Max-with-comparer
// or Average-with-selector, 1-/2-arg MinBy/MaxBy), so the argument-count ceiling for these five method
// names is raised to 2 rather than reusing First/Single/Last/Aggregate's ceiling of 1.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyArrayLiteralFirstOrSingleOrLastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0290";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "First/Single/Last/Aggregate/Average/Min/Max/MinBy/MaxBy on an empty array literal always throws",
        messageFormat: "'xs.{0}(...)' always throws InvalidOperationException at runtime — the receiver array literal has no elements",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.First/Single/Last (with or without a predicate), the no-seed Aggregate(func), and the non-nullable-value-type overloads of Average/Min/Max/MinBy/MaxBy throw InvalidOperationException when the source sequence has no elements, provable here because the receiver is a freshly created array literal with a known, empty element count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "First" or "Single" or "Last" or "Aggregate" or "Average" or "Min" or "Max" or "MinBy" or "MaxBy",
            } access)
        {
            return;
        }
        var methodName = access.Name.Identifier.Text;
        var isTypeDependent = methodName is "Average" or "Min" or "Max" or "MinBy" or "MaxBy";
        var argCeiling = isTypeDependent ? 2 : 1;
        if (invocation.ArgumentList.Arguments.Count > argCeiling) return;

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
        if (isTypeDependent
            && (!method.ReturnType.IsValueType || method.ReturnType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
        {
            return;
        }

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
