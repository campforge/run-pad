using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer (RPAD0271): `new
// ArraySegment<T>(array, offset, count)` throws ArgumentOutOfRangeException for a negative
// `offset` or `count`, but a DIFFERENT exception type, ArgumentException, when `offset + count`
// exceeds the array's length — confirmed empirically via scratch `dotnet run` script (unlike
// AsSpan/AsMemory, which throw ArgumentOutOfRangeException uniformly for every invalid case). The
// 2-arg `(array)`-only overload (offset 0, count = array.Length) never throws given a literal array,
// so it's out of scope by construction — only the 3-arg overload is checked. Scoped to a LITERAL
// array whose length is known at the call site, same helper as the rest of this catalog's
// "literal receiver" family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArraySegmentConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0324";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ArraySegment constructor arguments are out of range",
        messageFormat: "'{0}' always throws {1} at runtime — offset ({2}) and count ({3}) don't fit within a {4}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new ArraySegment<T>(array, offset, count) throws ArgumentOutOfRangeException when offset or count is negative, and ArgumentException when offset + count exceeds the array's length.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arrayArg }, { Expression: var offsetArg }, { Expression: var countArg }] })
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is not { } length) return;
        if (context.SemanticModel.GetConstantValue(offsetArg, ct).Value is not int offset) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        string exceptionType;
        if (offset < 0 || count < 0)
        {
            exceptionType = "ArgumentOutOfRangeException";
        }
        else if (offset + count > length)
        {
            exceptionType = "ArgumentException";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.OriginalDefinition.ToDisplayString() != "System.ArraySegment<T>") return;
        if (ctor.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), exceptionType, offset, count, length));
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
