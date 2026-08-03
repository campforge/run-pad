using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести восемьдесят первый проход, batch) — own pick, not a Roslynator port. Same
// "literal array with a bad index/count combination always throws" theme as
// InvalidStringCharArrayConstructorArgumentAnalyzer (RPAD0269, проход 280): `Encoding.GetString(byte[]
// bytes, int index, int count)` throws ArgumentOutOfRangeException when index or count is negative, or
// when `index + count` exceeds the array's length (confirmed empirically before writing this — a
// single exception type across every invalid case, like RPAD0269, not two different ones like
// InvalidBitConverterArgumentAnalyzer/RPAD0268). Scoped to a LITERAL array whose length is known at the
// call site, same helper as RPAD0266-0269.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEncodingGetStringArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0270";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Encoding.GetString(byte[], index, count) arguments are out of range",
        messageFormat: "'Encoding.GetString(...)' always throws ArgumentOutOfRangeException at runtime — index ({0}) and count ({1}) don't fit within a {2}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Encoding.GetString(byte[], int index, int count) throws ArgumentOutOfRangeException when index or count is negative, or index + count exceeds the array's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var indexArg }, { Expression: var countArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.Encoding") return;

        if (index >= 0 && count >= 0 && index + count <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), index, count, arrayLength));
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
