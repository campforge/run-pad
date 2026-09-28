using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// array with a bad index/length combination always throws" theme as InvalidArrayFillArgumentAnalyzer
// (RPAD0294), applied to the 4-/5-arg `Array.BinarySearch(Array array, int index, int length, object
// value[, IComparer comparer])` overload's UPPER-bound case specifically: confirmed empirically (via
// scratch `dotnet run` script) that `index + length` exceeding the array's own known literal length
// throws ArgumentException — a genuinely DIFFERENT exception type from RPAD0294's family (which is
// entirely ArgumentOutOfRangeException), so this is a new analyzer rather than an RPAD0294 extension.
// Deliberately does NOT duplicate the NEGATIVE index/length case already covered by
// NegativeArrayResizeOrClearArgumentAnalyzer (RPAD0263, which already includes `BinarySearch` in its
// own negative-literal-only target set) — this analyzer bails out early whenever either is negative,
// leaving that check to RPAD0263, same precedent as RPAD0318's `Array.Copy` extension.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayBinarySearchRangeArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0359";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Array.BinarySearch index/length exceed the array's length",
        messageFormat: "'{0}' always throws ArgumentException at runtime — index ({1}) and length ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The 4-/5-arg Array.BinarySearch(array, index, length, value[, comparer]) throws ArgumentException when index + length exceeds the array's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "BinarySearch" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var indexArg }, { Expression: var lengthArg }, _, ..])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is not { } length) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int count) return;
        if (index < 0 || count < 0) return;
        if (index + count <= length) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "BinarySearch" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), index, count, length));
    }

    private static int? TryGetArrayLength(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
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
