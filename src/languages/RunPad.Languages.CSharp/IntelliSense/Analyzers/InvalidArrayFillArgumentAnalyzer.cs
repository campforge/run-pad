using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// array with a bad startIndex/count combination always throws" theme as
// InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer/InvalidConvertToBase64StringArgumentAnalyzer
// (RPAD0271/RPAD0272), applied to a syntactic shape neither of those cover: the 4-argument overload
// `Array.Fill<T>(T[] array, T value, int startIndex, int count)`. Confirmed empirically (scratch
// `dotnet run` script, since deleted) that ArgumentOutOfRangeException is thrown for a negative
// startIndex, a negative count, OR startIndex + count exceeding the array's length — the exact same
// boundary formula as the rest of this family, just at argument positions 2/3 instead of 1/2 (argument
// 1 here is the fill `value`, not part of the bounds check). Scoped to a LITERAL array whose length is
// known at the call site, same helper as RPAD0266-0272 (duplicated rather than shared, matching this
// project's existing precedent of not factoring out this small helper across files). The 2-argument
// overload (`Array.Fill(array, value)`, no startIndex/count) is naturally excluded by the argument-count
// pattern match.
// Extended in the триста двадцать восьмой проход to also cover `Array.IndexOf<T>(T[] array, T value, int
// startIndex, int count)` — confirmed empirically to throw under the IDENTICAL boundary formula as
// `Fill` (negative startIndex, negative count, or startIndex + count exceeding the array's length), and
// at the exact same argument positions (2/3), so it reuses the same branch rather than duplicating it.
// Deliberately does NOT extend to `Array.LastIndexOf`/`Array.FindLastIndex` — considered alongside this
// during the same empirical check and confirmed to use a DIFFERENT, backward-searching boundary formula
// (`startIndex` is the search's start point going toward index 0, roughly `startIndex - count + 1 >= 0`
// instead of `startIndex + count <= length`) — a genuinely different check, left as a possible future
// analyzer rather than shoehorned into this one. Class name/`DiagnosticId` kept as-is despite the scope
// growing beyond `Array.Fill`, same precedent as other same-mechanism extensions in this catalog.
// Extended again in the триста двадцать девятый проход to also cover `Array.FindIndex<T>(T[] array, int
// startIndex, int count, Predicate<T> match)` — confirmed empirically to throw under the SAME
// forward-searching boundary formula as `Fill`/`IndexOf`, but with a DIFFERENT argument SHAPE (`array,
// startIndex, count, predicate` — no `value` argument at position 1, so `startIndex`/`count` sit at
// positions 1/2 instead of 2/3). Handled via a second, shape-aware branch in `Analyze` rather than
// forcing it through the shared 4-argument destructuring pattern used by `Fill`/`IndexOf`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayFillArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0294";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Array.Fill/IndexOf/FindIndex startIndex/count arguments are out of range",
        messageFormat: "'Array.{0}(...)' always throws ArgumentOutOfRangeException at runtime — startIndex ({1}) and count ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Array.Fill<T>(T[] array, T value, int startIndex, int count), Array.IndexOf<T>(T[] array, T value, int startIndex, int count), and Array.FindIndex<T>(T[] array, int startIndex, int count, Predicate<T> match) throw ArgumentOutOfRangeException when startIndex or count is negative, or startIndex + count exceeds the array's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Fill" or "IndexOf" or "FindIndex" })
            return;
        var methodName = ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.Text;

        ExpressionSyntax arrayArg, startIndexArg, countArg;
        if (methodName == "FindIndex")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var findArrayArg }, { Expression: var findStartArg }, { Expression: var findCountArg }, _])
                return;
            (arrayArg, startIndexArg, countArg) = (findArrayArg, findStartArg, findCountArg);
        }
        else
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var otherArrayArg }, _, { Expression: var otherStartArg }, { Expression: var otherCountArg }])
                return;
            (arrayArg, startIndexArg, countArg) = (otherArrayArg, otherStartArg, otherCountArg);
        }

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;

        if (startIndex >= 0 && count >= 0 && startIndex + count <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, startIndex, count, arrayLength));
    }

    private static int? TryGetArrayLength(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
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
        if (expression is CollectionExpressionSyntax collectionExpr)
            return collectionExpr.Elements.Count;
        return null;
    }
}
