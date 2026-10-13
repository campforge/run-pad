using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. The backward-searching
// counterpart to InvalidArrayFillArgumentAnalyzer's (RPAD0294) forward-searching
// `Fill`/`IndexOf`/`FindIndex` family, deliberately kept as a SEPARATE analyzer rather than folded into
// that one — the boundary formula is genuinely different, not just a different argument shape: for
// `Array.LastIndexOf<T>(T[] array, T value, int startIndex, int count)` and `Array.FindLastIndex<T>(T[]
// array, int startIndex, int count, Predicate<T> match)`, the search runs BACKWARD from `startIndex`
// toward index 0, so the valid range is `0 <= startIndex < array.Length` (note: `<`, not `<=` —
// `startIndex == array.Length` always throws here, unlike the forward-searching family where that's the
// valid empty-tail case) AND `count >= 0` AND `startIndex - count + 1 >= 0` — confirmed empirically via
// scratch `dotnet run` script, not assumed, including the boundary case `startIndex - count + 1 == 0`
// (valid) vs `== -1` (throws). Deliberately SKIPS zero-length arrays entirely (`arrayLength == 0` bails
// out before checking anything) — the same empirical check found a genuinely surprising BCL leniency
// there (`LastIndexOf` on an empty array accepts BOTH `startIndex == -1` AND `startIndex == 0` with
// `count == 0`, an inconsistency not worth encoding into this analyzer's logic when the realistic value
// of flagging bad calls against an intentionally-empty literal array is close to zero anyway). Handled
// via the same two-branch, shape-aware `Analyze` structure as RPAD0294's `FindIndex` extension (value-
// bearing 4-arg shape for `LastIndexOf`, non-value 4-arg shape for `FindLastIndex`), and the same
// duplicated `TryGetArrayLength` helper (RPAD0266-0294 precedent of not factoring this out across files).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayLastIndexOfArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0298";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Array.LastIndexOf/FindLastIndex startIndex/count arguments are out of range",
        messageFormat: "'Array.{0}(...)' always throws ArgumentOutOfRangeException at runtime — startIndex ({1}) and count ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Array.LastIndexOf<T>(T[] array, T value, int startIndex, int count) and Array.FindLastIndex<T>(T[] array, int startIndex, int count, Predicate<T> match) throw ArgumentOutOfRangeException when startIndex is not within the array, count is negative, or startIndex - count + 1 is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "LastIndexOf" or "FindLastIndex" })
            return;
        var methodName = ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.Text;

        ExpressionSyntax arrayArg, startIndexArg, countArg;
        if (methodName == "FindLastIndex")
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
        if (arrayLength is not > 0) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;

        if (startIndex >= 0 && startIndex < arrayLength && count >= 0 && startIndex - count + 1 >= 0) return;

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
