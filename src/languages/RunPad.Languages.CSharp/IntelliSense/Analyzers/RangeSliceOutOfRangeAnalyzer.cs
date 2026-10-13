using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling of
// ArrayLiteralIndexerOutOfRangeAnalyzer/StringLiteralIndexerOutOfRangeAnalyzer (RPAD0283/RPAD0282,
// extended in the same change for the `^N` Index form) — this one handles the OTHER indexer sugar form,
// `Range` slicing (`arr[2..10]`, `"abc"[2..10]`): both throw `ArgumentOutOfRangeException` when the
// resolved slice doesn't fit the receiver's own known length — confirmed empirically via scratch
// `dotnet run` script, including the two distinct ways it can fail (the end bound exceeding the
// receiver's length, or the end bound landing before the start bound, i.e. a negative computed slice
// length) and that BOTH array and string share the exact same exception type here (unlike the plain
// Index/`^N` form, where array/string share `IndexOutOfRangeException` but List<T> instead throws
// `ArgumentOutOfRangeException` for `^N` — Range slicing's story is simpler: confirmed identical for
// array and string, so one analyzer covers both). Deliberately kept SEPARATE from RPAD0283/RPAD0282
// rather than folded in — a Range slice reads a whole new sub-collection, not a single element, a
// genuinely different mechanism, matching this catalog's established precedent for splitting by
// exception-type/mechanism (e.g. RPAD0357 vs RPAD0358). Also originally restricted to a plain
// non-negative integer literal (or absent, defaulting to 0/the receiver's own length) on EACH side of
// the `..`, deferring both List<T> and any `^N` operand inside a Range as further, more involved cases.
// Extended to close both of those deferred cases:
// 1. `^N` operand INSIDE a Range (`arr[^2..]`, `arr[..^5]`) — confirmed empirically to resolve to the
//    exact same `ArgumentOutOfRangeException` this analyzer already reports for array/string, so
//    `TryGetBound` simply grew a branch resolving `^N` to `totalLength - N` first, reusing the existing
//    bounds check unchanged. Confirmed both sides can use `^N` simultaneously (`arr[^3..^1]`) without
//    issue, since each side resolves independently.
// 2. `List<T>` Range slicing (`new List<int> { 1, 2, 3 }[2..10]`) — deliberately deferred when this
//    analyzer was first written, because its exception-type story is a genuine three-way split, unlike
//    array/string's single uniform type: confirmed empirically that `List<T>`'s Range indexer lowers to
//    `GetRange(index, count)`, which throws `ArgumentOutOfRangeException` for a NEGATIVE `index` OR a
//    negative COMPUTED `count` (i.e. end-before-start, matching `Parameter 'index'`/`Parameter 'count'`
//    respectively — both the same exception TYPE, so collapsed into one condition here), but
//    `ArgumentException` when both are non-negative yet `index + count` exceeds the receiver's own known
//    length. The exception-type text is now a message-format PARAMETER rather than baked into the format
//    string (same technique already used by RPAD0303's `messageFormat`), chosen so the array/string
//    branch's RENDERED text is byte-for-byte unchanged before this change — no existing test needed
//    updating for that branch.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RangeSliceOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0363";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Range slice is out of bounds for an array, string, or List<T> literal",
        messageFormat: "'{0}' always throws {1} at runtime — the receiver has only {2} {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Range-slicing (arr[a..b]) an array, string, or List<T> literal throws when the start index is negative, the end index exceeds the receiver's own known length, or the end index is before the start index — ArgumentOutOfRangeException for array/string always, and for List<T> when the start/computed-count is negative; ArgumentException for List<T> when the end index exceeds the receiver's length.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElementAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: RangeExpressionSyntax range }]) return;

        var ct = context.CancellationToken;
        string noun;
        int totalLength;
        bool isList;
        if (elementAccess.Expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
        {
            totalLength = explicitInit.Expressions.Count;
            noun = "element(s)";
            isList = false;
        }
        else if (elementAccess.Expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
        {
            totalLength = implicitInit.Expressions.Count;
            noun = "element(s)";
            isList = false;
        }
        else if (elementAccess.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } stringLiteral
            && context.SemanticModel.GetConstantValue(stringLiteral, ct).Value is string receiverValue)
        {
            totalLength = receiverValue.Length;
            noun = "character(s)";
            isList = false;
        }
        else if (TryGetListLiteralElementCount(elementAccess.Expression, context.SemanticModel, ct) is { } listCount)
        {
            totalLength = listCount;
            noun = "element(s)";
            isList = true;
        }
        else
        {
            return;
        }

        if (!TryGetBound(range.LeftOperand, 0, totalLength, out var start)) return;
        if (!TryGetBound(range.RightOperand, totalLength, totalLength, out var end)) return;

        string exceptionType;
        if (start >= 0 && end >= start && end <= totalLength) return;
        if (!isList)
        {
            exceptionType = "ArgumentOutOfRangeException";
        }
        else if (start < 0 || end < start)
        {
            exceptionType = "ArgumentOutOfRangeException";
        }
        else if (end > totalLength)
        {
            exceptionType = "ArgumentException";
        }
        else
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), exceptionType, totalLength, noun));
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

    private static bool TryGetBound(ExpressionSyntax? operand, int defaultValue, int totalLength, out int value)
    {
        if (operand is null)
        {
            value = defaultValue;
            return true;
        }
        if (operand is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } fromEndLiteral })
        {
            if (fromEndLiteral.Token.Value is not int fromEndValue) { value = 0; return false; }
            value = totalLength - fromEndValue;
            return true;
        }
        if (operand is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negLiteral })
        {
            if (negLiteral.Token.Value is not int positive) { value = 0; return false; }
            value = -positive;
            return true;
        }
        if (operand is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal && literal.Token.Value is int literalValue)
        {
            value = literalValue;
            return true;
        }
        value = 0;
        return false;
    }
}
