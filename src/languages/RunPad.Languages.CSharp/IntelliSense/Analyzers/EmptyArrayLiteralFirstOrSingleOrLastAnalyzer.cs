using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283), applied
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
// NegativeElementAtIndexAnalyzer's positive-range extension (RPAD0236).
// Extended to also cover the single-argument (no-seed)
// `.Aggregate(func)` overload — confirmed empirically to throw InvalidOperationException for an empty
// receiver with no element-type nuance (unlike Max/Min, still out of scope). The two-argument
// `Aggregate(seed, func)` overload does NOT throw for an empty sequence (it just returns `seed`), but
// needs no special-casing here — it's already excluded by the existing `Arguments.Count > 1` guard.
// Class name/`DiagnosticId` kept as-is despite the scope growing beyond First/Single/Last, same
// precedent as other same-mechanism extensions in this catalog.
// Extended to also cover `.Average()`/`.Min()`/`.Max()`/`.MinBy(...)`/
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
// Extended to also recognize `ImmutableArray<T>.Empty`/
// `ImmutableList<T>.Empty`/`ImmutableHashSet<T>.Empty`/`ImmutableSortedSet<T>.Empty`/
// `ImmutableStack<T>.Empty`/`ImmutableQueue<T>.Empty` as a known-empty receiver, alongside the existing
// array-literal one — confirmed empirically (scratch `dotnet run` script) that every one of the six
// throws identically (`InvalidOperationException`, "Sequence contains no elements") for `.First()`/
// `.Min()` etc., since they're all plain `IEnumerable<T>` sequences underneath. Message format's
// "array literal" wording generalized to just "the receiver", since it's no longer always an array.
// Self-correction: a Theory row for `ImmutableStack<int>.Empty.First()`/
// `ImmutableQueue<int>.Empty.First()` failed — the exact same `Empty`-is-a-PROPERTY-not-a-FIELD gap
// caught and fixed in sibling EmptyImmutableStackOrQueuePeekOrPopAnalyzer (RPAD0360), just
// not yet applied here when this extension was first written; fixed the same way, accepting either
// `IFieldSymbol` or `IPropertySymbol` named `Empty`. Separately, a Theory row using
// `ImmutableSortedSet<int>.Empty.Min()` was a bad TEST case, not an analyzer bug: reflection confirmed
// `ImmutableSortedSet<T>` (unlike the other five types touched this change) has its own instance
// PROPERTY named `Min`/`Max` (returning `T`), which C# member lookup resolves in preference to the
// `Enumerable.Min()` extension entirely — so `.Min()` on it doesn't invoke the LINQ method this analyzer
// is about at all. Fixed by using `.Aggregate(...)` for that row's SortedSet case instead. A THIRD,
// more subtle self-correction: even after both fixes above, `ImmutableArray<int>.Empty.First()` alone
// still failed. A dedicated symbol-resolution check (real `CSharpCompilation` + `GetSymbolInfo`, not
// reflection) revealed `ImmutableArray<T>` — unlike the other five types — has its OWN specialized,
// non-boxing `First`/`Single`/`Last`/`Aggregate` overloads in `System.Linq.ImmutableArrayExtensions`
// (`Average`/`Min`/`Max`/`MinBy`/`MaxBy` are NOT among them, confirmed via reflection — those five still
// resolve to plain `System.Linq.Enumerable` even for an `ImmutableArray<T>` receiver, so needed no
// change), and C# overload resolution prefers this more-specific, non-boxing overload over `Enumerable`'s
// whenever both are in scope. `First`/`Single`/`Last` on `ImmutableArrayExtensions` throw identically to
// `Enumerable`'s for an empty receiver (confirmed empirically) — but `Aggregate`'s one-arg (no-seed)
// overload on `ImmutableArrayExtensions` does NOT throw for an empty receiver (confirmed empirically,
// genuinely different semantics from `Enumerable.Aggregate`'s same-shaped overload, which does throw).
// Fixed by accepting `System.Linq.ImmutableArrayExtensions` as a second known containing type, but ONLY
// for `methodName is "First" or "Single" or "Last"` — an explicit allow-list rather than merely excluding
// `Aggregate`, so any future method added to this analyzer's tracked name set defaults to being rejected
// from this second containing type unless individually verified, not silently accepted.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyArrayLiteralFirstOrSingleOrLastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0290";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "First/Single/Last/Aggregate/Average/Min/Max/MinBy/MaxBy on an empty array literal always throws",
        messageFormat: "'xs.{0}(...)' always throws InvalidOperationException at runtime — the receiver has no elements",
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
        var containingTypeDisplay = method.ContainingType?.ToDisplayString();
        var isKnownContainingType = containingTypeDisplay == "System.Linq.Enumerable"
            || (containingTypeDisplay == "System.Linq.ImmutableArrayExtensions" && methodName is "First" or "Single" or "Last");
        if (!isKnownContainingType) return;
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

        if (expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } emptyAccess)
        {
            var emptyType = semanticModel.GetSymbolInfo(emptyAccess, ct).Symbol switch
            {
                IFieldSymbol { Name: "Empty" } field => field.ContainingType,
                IPropertySymbol { Name: "Empty" } property => property.ContainingType,
                _ => null,
            };
            if (emptyType is { } fieldType && IsKnownEmptyableImmutableCollectionType(fieldType.OriginalDefinition.ToDisplayString()))
                return 0;
        }

        return null;
    }

    private static bool IsKnownEmptyableImmutableCollectionType(string displayString) => displayString is
        "System.Collections.Immutable.ImmutableArray<T>" or
        "System.Collections.Immutable.ImmutableList<T>" or
        "System.Collections.Immutable.ImmutableHashSet<T>" or
        "System.Collections.Immutable.ImmutableSortedSet<T>" or
        "System.Collections.Immutable.ImmutableStack<T>" or
        "System.Collections.Immutable.ImmutableQueue<T>";
}
