using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283, проход 300), applied
// to a freshly created `List<T>` collection-initializer instead of an array literal: `new List<int> { 1,
// 2, 3 }[10]`/`.RemoveAt(10)`/`.Insert(10, x)` all throw when the index is out of range for the
// initializer's own known element count — confirmed empirically (scratch `dotnet run` script, since
// deleted), and confirmed the exception type is `ArgumentOutOfRangeException` (List<T>'s indexer is a
// plain property, unlike Array's special-cased `IndexOutOfRangeException`). Unlike RPAD0283, this covers
// NEGATIVE indices too — `List<T>`'s indexer isn't subject to the compiler's CS0251 warning (that's
// specific to actual array-typed expressions), so there's no existing coverage to defer to. `Insert`
// has a genuinely different boundary than the indexer/`RemoveAt` — `index == count` is VALID for
// `Insert` (append at the end) but invalid for the other two (there's no element there to read/remove) —
// confirmed empirically, handled via a per-method boundary check. Scoped to the classic `new List<T> {
// ... }` object-creation-with-collection-initializer syntax only (not the `[1, 2, 3]` collection-
// expression form), matching RPAD0283's own scope precedent for this same reason.
// Extended in the триста сорок девятый проход to also cover `.RemoveRange(index, count)`/`.GetRange
// (index, count)` — confirmed empirically to throw `ArgumentException` (a genuinely DIFFERENT exception
// type from the indexer/`RemoveAt`/`Insert` family — confirmed rather than assumed) when `index`/`count`
// are negative or `index + count` exceeds the literal's own known element count. Deliberately NOT
// folded into `NegativeListIndexOrCountArgumentAnalyzer` (RPAD0287, which already covers
// `RemoveRange`/`GetRange` too) — that analyzer's whole scope is intentionally negativity-only, needing
// no receiver length at all (works even on a non-literal `List<T>` parameter); this new check is
// length-dependent, exactly this analyzer's own theme, not RPAD0287's, so it belongs here instead,
// alongside the same-theme indexer/`RemoveAt`/`Insert` checks. The message format is now parameterized
// with the exception type name to describe either family.
// Extended in the триста пятьдесят четвёртый проход to also cover `.InsertRange(index, collection)`
// (confirmed empirically to share `Insert`'s EXACT boundary rule — `index` in `[0, count]` inclusive —
// and exception type, so it's simply added alongside `Insert` in the same index-only branch; `Insert`
// Range` isn't in RPAD0287's own target set, so there's no overlap to defer to) and the 4-arg
// `.BinarySearch(index, count, item, comparer)` overload for the LENGTH-exceeding case only — `Binary
// Search` throws `ArgumentException` when `index + count` exceeds the literal's known element count.
// Extended again in the триста пятьдесят пятый проход to also cover the 3-arg
// `.IndexOf(item, index, count)` overload, again for the LENGTH-exceeding case only —
// `ArgumentOutOfRangeException` when `index + count` exceeds the literal's known element count.
// Self-correction (триста пятьдесят пятый проход): the `BinarySearch` branch originally ALSO
// re-reported the negative-`index`/`count` case (with `ArgumentOutOfRangeException`), and the new
// `IndexOf` branch was initially written the same way — but `BinarySearch` AND `IndexOf` are BOTH
// already in `NegativeListIndexOrCountArgumentAnalyzer`'s (RPAD0287) own target method set, which
// already reports negative `index`/`count` for exactly these two methods. A dedicated PROBE for the
// negative-index sub-case specifically (`new List<int> { 1, 2, 3 }.IndexOf(1, -1, 2)`) confirmed BOTH
// RPAD0287 and this analyzer firing together on the same call for the same underlying defect — a
// genuine double-message this project's discipline explicitly guards against. Both branches now defer
// entirely to RPAD0287 for negativity (early-return whenever `index < 0 || count < 0`) and only report
// the length-exceeding case here, matching the intentional split already used for `RemoveRange`/
// `GetRange` vs. RPAD0287. (`RemoveRange`/`GetRange` themselves were left as-is — that split predates
// this проход and is out of scope for today's fix.)
// Extended in the триста шестьдесят шестой проход to also handle the `^N` Index-form single-element
// indexer (`new List<int> { 1, 2, 3 }[^5]`) on the plain indexer only — confirmed empirically that
// `List<T>` supports `^`/`Index` sugar (it has the `Count` property + `int` indexer pattern the compiler
// looks for) but NOT `RemoveAt(^N)`/`Insert(^N, x)` (those take a plain `int` parameter, which has no
// implicit conversion from `Index`, so `^N` there is a compile error, not a runtime concern — confirmed
// via a real `dotnet build`, not assumed). Resolved to a plain offset (`count - N`) and fed through the
// same bounds check as the existing literal-index branch, same `ArgumentOutOfRangeException` type
// (confirmed via scratch `dotnet run` script — same exception type as the plain-int indexer, unlike
// array's `^N`/RPAD0283, which stays `IndexOutOfRangeException`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ListLiteralIndexerOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0303";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for a List<T> literal",
        messageFormat: "'{0}' always throws {2} at runtime — this List<T> literal has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing, RemoveAt, or Insert on a freshly created List<T> collection initializer with an index outside its own known element count throws ArgumentOutOfRangeException; RemoveRange/GetRange with an out-of-range index/count throws ArgumentException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeElementAccess, SyntaxKind.ElementAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;

        var elementCount = TryGetListLiteralElementCount(elementAccess.Expression, context.SemanticModel, context.CancellationToken);
        if (elementCount is not { } count) return;

        if (TryGetIntLiteralValue(indexArg, out var index))
        {
            if (index >= 0 && index < count) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), count, "ArgumentOutOfRangeException"));
            return;
        }

        if (indexArg is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } fromEndLiteral }
            && fromEndLiteral.Token.Value is int fromEndValue)
        {
            var resolvedIndex = count - fromEndValue;
            if (resolvedIndex >= 0 && resolvedIndex < count) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), count, "ArgumentOutOfRangeException"));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "RemoveAt" or "Insert" or "InsertRange" or "RemoveRange" or "GetRange" or "BinarySearch" or "IndexOf",
            } access)
        {
            return;
        }
        var methodName = access.Name.Identifier.Text;
        var ct = context.CancellationToken;

        if (methodName == "IndexOf")
        {
            if (invocation.ArgumentList.Arguments is not [_, { Expression: var ioIndexArg }, { Expression: var ioCountArg }])
                return;
            if (!TryGetIntLiteralValue(ioIndexArg, out var ioIndex)) return;
            if (!TryGetIntLiteralValue(ioCountArg, out var ioCount)) return;
            // Negative index/count is already covered by NegativeListIndexOrCountArgumentAnalyzer
            // (RPAD0287) — defer to it entirely rather than double-reporting the same defect.
            if (ioIndex < 0 || ioCount < 0) return;

            var ioElementCount = TryGetListLiteralElementCount(access.Expression, context.SemanticModel, ct);
            if (ioElementCount is not { } ioTotal) return;
            if (ioIndex + ioCount <= ioTotal) return;

            if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "IndexOf", Parameters.Length: 3 })
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), ioTotal, "ArgumentOutOfRangeException"));
            return;
        }

        if (methodName == "BinarySearch")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var bsIndexArg }, { Expression: var bsCountArg }, _, _])
                return;
            if (!TryGetIntLiteralValue(bsIndexArg, out var bsIndex)) return;
            if (!TryGetIntLiteralValue(bsCountArg, out var bsCount)) return;
            // Negative index/count is already covered by NegativeListIndexOrCountArgumentAnalyzer
            // (RPAD0287) — defer to it entirely rather than double-reporting the same defect.
            if (bsIndex < 0 || bsCount < 0) return;

            var bsElementCount = TryGetListLiteralElementCount(access.Expression, context.SemanticModel, ct);
            if (bsElementCount is not { } bsTotal) return;
            if (bsIndex + bsCount <= bsTotal) return;

            if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "BinarySearch", Parameters.Length: 4 })
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), bsTotal, "ArgumentException"));
            return;
        }

        if (methodName is "RemoveRange" or "GetRange")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var rangeIndexArg }, { Expression: var rangeCountArg }])
                return;
            if (!TryGetIntLiteralValue(rangeIndexArg, out var rangeIndex)) return;
            if (!TryGetIntLiteralValue(rangeCountArg, out var rangeCount)) return;

            var rangeElementCount = TryGetListLiteralElementCount(access.Expression, context.SemanticModel, ct);
            if (rangeElementCount is not { } rangeTotal) return;
            if (rangeIndex >= 0 && rangeCount >= 0 && rangeIndex + rangeCount <= rangeTotal) return;

            if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedRangeName }
                || resolvedRangeName != methodName)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), rangeTotal, "ArgumentException"));
            return;
        }

        ExpressionSyntax indexArg;
        if (methodName == "RemoveAt")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var removeAtIndexArg }]) return;
            indexArg = removeAtIndexArg;
        }
        else
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var insertIndexArg }, _]) return;
            indexArg = insertIndexArg;
        }
        if (!TryGetIntLiteralValue(indexArg, out var index)) return;

        var elementCount = TryGetListLiteralElementCount(access.Expression, context.SemanticModel, ct);
        if (elementCount is not { } count) return;
        var upperBoundExclusive = methodName is "Insert" or "InsertRange" ? count + 1 : count;
        if (index >= 0 && index < upperBoundExclusive) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName }
            || resolvedName != methodName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), count, "ArgumentOutOfRangeException"));
    }

    private static int? TryGetListLiteralElementCount(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        InitializerExpressionSyntax? initializer = expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitInit } => implicitInit,
            _ => null,
        };
        if (initializer is null || initializer.Kind() != SyntaxKind.CollectionInitializerExpression) return null;

        if (semanticModel.GetTypeInfo(expression, ct).Type is not { } type
            || type.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
        {
            return null;
        }

        return initializer.Expressions.Count;
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negLiteral,
            })
        {
            if (negLiteral.Token.Value is not int positive) return false;
            value = -positive;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            && literal.Token.Value is int positiveValue)
        {
            value = positiveValue;
            return true;
        }
        return false;
    }
}
