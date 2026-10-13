using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal array with a bad index/count combination always throws" theme as
// InvalidStringCharArrayConstructorArgumentAnalyzer (RPAD0269, проход 280): `Encoding.GetString(byte[]
// bytes, int index, int count)` throws ArgumentOutOfRangeException when index or count is negative, or
// when `index + count` exceeds the array's length (confirmed empirically before writing this — a
// single exception type across every invalid case, like RPAD0269, not two different ones like
// InvalidBitConverterArgumentAnalyzer/RPAD0268). Scoped to a LITERAL array whose length is known at the
// call site, same helper as RPAD0266-0269.
// Extended (триста пятьдесят пятый проход, batch) to also cover `Encoding.GetByteCount(char[], int,
// int)`, `GetCharCount(byte[], int, int)`, and `GetChars(byte[], int, int)` — confirmed empirically to
// share the EXACT same boundary formula and exception type as `GetString`. Also extended to check the
// SOURCE-array bounds (only) of the 5-arg `GetBytes(char[] chars, int charIndex, int charCount, byte[]
// bytes, int byteIndex)` overload — the source `chars` bounds follow the identical formula, but the
// destination `byteIndex` validity depends on how many bytes the encoding will actually produce (not a
// simple literal-derivable bound the way the other checks are), so that half is intentionally left
// unchecked.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEncodingGetStringArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0270";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Encoding array/index/count arguments are out of range",
        messageFormat: "'Encoding.{0}(...)' always throws ArgumentOutOfRangeException at runtime — index ({1}) and count ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Encoding.GetString/GetByteCount/GetCharCount/GetChars(array, int index, int count), and the source half of GetBytes(char[], int, int, byte[], int), throw ArgumentOutOfRangeException when index or count is negative, or index + count exceeds the source array's length.");

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
                Name.Identifier.Text: "GetString" or "GetByteCount" or "GetCharCount" or "GetChars" or "GetBytes",
            } access)
        {
            return;
        }
        var methodName = access.Name.Identifier.Text;
        var arguments = invocation.ArgumentList.Arguments;
        var expectedArgCount = methodName == "GetBytes" ? 5 : 3;
        if (arguments.Count != expectedArgCount) return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arguments[0].Expression, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value is not int index) return;
        if (context.SemanticModel.GetConstantValue(arguments[2].Expression, ct).Value is not int count) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Text.Encoding") return;

        if (index >= 0 && count >= 0 && index + count <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, index, count, arrayLength));
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
