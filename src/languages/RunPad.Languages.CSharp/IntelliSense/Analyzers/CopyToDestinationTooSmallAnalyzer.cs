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
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CopyToDestinationTooSmallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0318";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CopyTo destination array is too small",
        messageFormat: "'{0}' always throws ArgumentException at runtime — the destination array has only {1} element(s), not enough for {2} source element(s) starting at index {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "List<T>.CopyTo(array)/CopyTo(array, index), HashSet<T>.CopyTo(array), and Array.CopyTo(array, index) throw ArgumentException when the destination array doesn't have enough room for the source's elements.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.HashSet<T>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
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

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), dest, source, index));
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
