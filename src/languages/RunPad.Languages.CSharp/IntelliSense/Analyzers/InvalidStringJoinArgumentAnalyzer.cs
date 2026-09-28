using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста тридцать пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// array with a bad startIndex/count combination always throws" theme as
// InvalidArrayFillArgumentAnalyzer (RPAD0294, проходы 325/328/329), on a genuinely different receiver
// shape: `string.Join(string separator, string[] value, int startIndex, int count)` is a STATIC method
// where the array sits at argument position 1 (not 0 — `separator` is argument 0), followed by
// `startIndex`/`count` at positions 2/3. Confirmed empirically (scratch `dotnet run` script, since
// deleted) that ArgumentOutOfRangeException is thrown under the IDENTICAL forward-searching boundary
// formula as the rest of this family (negative startIndex, negative count, or startIndex + count
// exceeding the array's length). Scoped to a LITERAL `string[]` array whose length is known at the call
// site, same helper as RPAD0266-0298 (duplicated rather than shared, matching this project's existing
// precedent). The separator argument's content is irrelevant to the bounds check and is never inspected.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringJoinArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0301";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "string.Join startIndex/count arguments are out of range",
        messageFormat: "'string.Join(...)' always throws ArgumentOutOfRangeException at runtime — startIndex ({0}) and count ({1}) don't fit within a {2}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Join(string separator, string[] value, int startIndex, int count) throws ArgumentOutOfRangeException when startIndex or count is negative, or startIndex + count exceeds the array's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Join" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var arrayArg }, { Expression: var startIndexArg }, { Expression: var countArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Join", IsStatic: true } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (startIndex >= 0 && count >= 0 && startIndex + count <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), startIndex, count, arrayLength));
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
