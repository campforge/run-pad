using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок восьмой проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidArrayFillArgumentAnalyzer (RPAD0294), applied to `.CopyTo`
// instead of a startIndex/count pair: `list.CopyTo(destinationArray)` (`List<T>`/`HashSet<T>`, the
// no-index overload) and `array.CopyTo(destinationArray, index)` (the 2-arg `Array`/`List<T>` overload)
// both throw ArgumentException when the destination doesn't have enough room for the source's own known
// element count starting at `index` — confirmed empirically via scratch `dotnet run` script, not
// assumed. Scoped to a LITERAL source (array/list/hashset initializer with a known element count) and a
// LITERAL destination array (same known-length requirement), reusing this catalog's established
// array/list-literal-length helpers (duplicated rather than shared, matching this project's existing
// precedent). The literal `index` argument (2-arg overload only) defaults to `0` when the call is the
// 1-argument overload.
// Extended (триста пятидесятый проход, batch) to also cover the STATIC `Array.Copy(source,
// destination, length)` (3-arg) and `Array.Copy(source, sourceIndex, destination, destinationIndex,
// length)` (5-arg) overloads — a genuinely different method (static, not an instance `.CopyTo`) with
// its own bounds-checking mechanism: confirmed empirically that it checks the SOURCE side first
// (ArgumentException "Source array was not long enough" when `sourceIndex + length` exceeds the
// source's own length, given a literal `sourceIndex` — 0 for the 3-arg overload), then the
// destination side (same message shape as the existing `.CopyTo` branch, when `destinationIndex +
// length` exceeds the destination's length). Deliberately does not duplicate the NEGATIVE
// sourceIndex/destinationIndex/length case already covered by
// NegativeArrayResizeOrClearArgumentAnalyzer (RPAD0263) for the very same `Array.Copy` overload — this
// extension bails out early whenever any of those three are negative, leaving that check to RPAD0263.
// Extended (триста пятьдесят четвёртый проход, batch) to also cover the 4-arg `List<T>.CopyTo(int
// index, T[] array, int arrayIndex, int count)` overload and `StringBuilder.CopyTo(int sourceIndex,
// char[] destination, int destinationIndex, int count)` — both throw ArgumentException with the same
// "destination too small" mechanism (`destinationIndex/arrayIndex + count` exceeding the destination
// array's known literal length) — confirmed empirically. Unlike the existing 1-/2-arg `.CopyTo` branch,
// this doesn't attempt to know the SOURCE's own element count (a `StringBuilder`'s content isn't
// generally knowable from its literal constructor the way a `List<T>`/array literal's element count
// is), so it only validates the destination side, same scope limitation as the `Array.Copy` branch's
// destination check.
// Extended (триста пятьдесят пятый проход, batch) to also cover `Queue<T>.CopyTo(array, index)` and
// `Stack<T>.CopyTo(array, index)` — confirmed empirically to throw ArgumentException with the same
// "destination too small" mechanism as the existing `List<T>`/`HashSet<T>` branch. Unlike those two
// types, `Queue<T>`/`Stack<T>` have no collection-initializer syntax (no public `Add` method — they
// use `Enqueue`/`Push`), so a literal instance is only expressible via `new Queue<T>(sourceArray)`/`new
// Stack<T>(sourceArray)` (the `IEnumerable<T>`-taking constructor) with a literal array argument — a
// genuinely different source-count extraction shape than the collection-initializer-based helper
// already used for `List<T>`/`HashSet<T>`, handled by its own dedicated branch/helper.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CopyToDestinationTooSmallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0318";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CopyTo destination array is too small",
        messageFormat: "'{0}' always throws ArgumentException at runtime — the {1} array has only {2} element(s), not enough for {3} element(s) starting at index {4}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "List<T>.CopyTo(array)/CopyTo(array, index), HashSet<T>.CopyTo(array), Array.CopyTo(array, index), and the static Array.Copy(source, destination, length)/Array.Copy(source, sourceIndex, destination, destinationIndex, length) throw ArgumentException when the destination (or, for Array.Copy, the source) array doesn't have enough room.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.HashSet<T>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeArrayCopy, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeFourArgCopyTo, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeQueueOrStackCopyTo, SyntaxKind.InvocationExpression);
    }

    private static readonly ImmutableHashSet<string> QueueOrStackTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>");

    private static void AnalyzeQueueOrStackCopyTo(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CopyTo" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var indexArg }])
            return;

        var ct = context.CancellationToken;
        var sourceCount = TryGetQueueOrStackLiteralElementCount(access.Expression, context.SemanticModel, ct);
        if (sourceCount is not { } source) return;
        var destLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (destLength is not { } dest) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index || index < 0) return;
        if (source + index <= dest) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CopyTo" } method)
            return;
        var containingTypeName = method.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containingTypeName is null || !QueueOrStackTypes.Contains(containingTypeName)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "destination", dest, source, index));
    }

    private static int? TryGetQueueOrStackLiteralElementCount(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        if (expression is not ObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var sourceArg }] })
            return null;
        if (semanticModel.GetTypeInfo(expression, ct).Type is not INamedTypeSymbol { IsGenericType: true } type
            || !QueueOrStackTypes.Contains(type.OriginalDefinition.ToDisplayString()))
        {
            return null;
        }
        return TryGetArrayLength(sourceArg, semanticModel, ct);
    }

    private static void AnalyzeFourArgCopyTo(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CopyTo" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var arrayArg }, { Expression: var arrayIndexArg }, { Expression: var countArg }])
            return;

        var ct = context.CancellationToken;
        var destLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (destLength is not { } dest) return;
        if (context.SemanticModel.GetConstantValue(arrayIndexArg, ct).Value is not int arrayIndex || arrayIndex < 0) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count || count < 0) return;
        if (arrayIndex + count <= dest) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CopyTo" } method)
            return;
        var containingTypeName = method.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containingTypeName is not ("System.Collections.Generic.List<T>" or "System.Text.StringBuilder"))
            return;
        if (method.Parameters is not
            [
            { Type.SpecialType: SpecialType.System_Int32 },
                _,
            { Type.SpecialType: SpecialType.System_Int32 },
            { Type.SpecialType: SpecialType.System_Int32 },
            ])
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "destination", dest, count, arrayIndex));
    }

    private static void AnalyzeArrayCopy(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Copy" })
            return;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not (3 or 5)) return;

        var ct = context.CancellationToken;
        ExpressionSyntax sourceArrayArg;
        ExpressionSyntax destArrayArg;
        ExpressionSyntax lengthArg;
        var sourceIndex = 0;
        var destIndex = 0;
        if (arguments.Count == 3)
        {
            sourceArrayArg = arguments[0].Expression;
            destArrayArg = arguments[1].Expression;
            lengthArg = arguments[2].Expression;
        }
        else
        {
            sourceArrayArg = arguments[0].Expression;
            destArrayArg = arguments[2].Expression;
            lengthArg = arguments[4].Expression;
            if (context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value is not int si) return;
            if (context.SemanticModel.GetConstantValue(arguments[3].Expression, ct).Value is not int di) return;
            sourceIndex = si;
            destIndex = di;
        }
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;
        if (sourceIndex < 0 || destIndex < 0 || length < 0) return;

        var sourceLength = TryGetArrayLength(sourceArrayArg, context.SemanticModel, ct);
        var destLength = TryGetArrayLength(destArrayArg, context.SemanticModel, ct);
        if (sourceLength is not { } source || destLength is not { } dest) return;

        string kind;
        int arrayLength;
        int index;
        if (sourceIndex + length > source)
        {
            kind = "source";
            arrayLength = source;
            index = sourceIndex;
        }
        else if (destIndex + length > dest)
        {
            kind = "destination";
            arrayLength = dest;
            index = destIndex;
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Copy" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), kind, arrayLength, length, index));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CopyTo" } access)
            return;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not (1 or 2)) return;

        var ct = context.CancellationToken;
        var sourceCount = TryGetSourceElementCount(access.Expression, context.SemanticModel, ct);
        if (sourceCount is not { } source) return;
        var destLength = TryGetArrayLength(arguments[0].Expression, context.SemanticModel, ct);
        if (destLength is not { } dest) return;

        var index = 0;
        if (arguments.Count == 2)
        {
            if (context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value is not int idx || idx < 0) return;
            index = idx;
        }
        if (source + index <= dest) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CopyTo" } method)
            return;
        if (method.ContainingType is not { } containingType) return;
        var isArraySource = containingType.ToDisplayString() == "System.Array";
        var isListOrHashSetSource = containingType.IsGenericType && TargetTypes.Contains(containingType.OriginalDefinition.ToDisplayString());
        if (!isArraySource && !isListOrHashSetSource) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "destination", dest, source, index));
    }

    private static int? TryGetSourceElementCount(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
            return explicitInit.Expressions.Count;
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return implicitInit.Expressions.Count;
        if (expression is CollectionExpressionSyntax collectionExpr)
            return collectionExpr.Elements.Count;

        InitializerExpressionSyntax? initializer = expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: { } objInit } => objInit,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitObjInit } => implicitObjInit,
            _ => null,
        };
        if (initializer is null || initializer.Kind() != SyntaxKind.CollectionInitializerExpression) return null;
        if (semanticModel.GetTypeInfo(expression, ct).Type is not INamedTypeSymbol { IsGenericType: true } type
            || !TargetTypes.Contains(type.OriginalDefinition.ToDisplayString()))
        {
            return null;
        }
        return initializer.Expressions.Count;
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
