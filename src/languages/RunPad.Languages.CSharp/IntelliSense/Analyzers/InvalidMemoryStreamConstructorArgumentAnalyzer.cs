using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidArraySegmentConstructorArgumentAnalyzer (RPAD0324, проход
// 350): `new MemoryStream(byte[] buffer, int index, int count[, bool writable])` on a literal array
// throws ArgumentOutOfRangeException for a negative `index`, but a DIFFERENT exception type,
// ArgumentException, when `index + count` exceeds the array's length — confirmed empirically via
// scratch `dotnet run` script (the exact same exception-type split as `ArraySegment<T>`'s constructor).
// Scoped to a LITERAL array whose length is known at the call site, same helper as the rest of this
// catalog's "literal receiver" family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMemoryStreamConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0335";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MemoryStream constructor arguments are out of range",
        messageFormat: "'{0}' always throws {1} at runtime — index ({2}) and count ({3}) don't fit within a {4}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new MemoryStream(buffer, index, count[, writable]) throws ArgumentOutOfRangeException when index is negative, and ArgumentException when index + count exceeds the buffer's length.");

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
        if (creation.ArgumentList is not { Arguments: { Count: 3 or 4 } args }) return;
        var (bufferArg, indexArg, countArg) = (args[0].Expression, args[1].Expression, args[2].Expression);

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(bufferArg, context.SemanticModel, ct);
        if (arrayLength is not { } length) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;

        string exceptionType;
        if (index < 0 || count < 0)
        {
            exceptionType = "ArgumentOutOfRangeException";
        }
        else if (index + count > length)
        {
            exceptionType = "ArgumentException";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.IO.MemoryStream") return;
        if (ctor.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, ..])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), exceptionType, index, count, length));
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
