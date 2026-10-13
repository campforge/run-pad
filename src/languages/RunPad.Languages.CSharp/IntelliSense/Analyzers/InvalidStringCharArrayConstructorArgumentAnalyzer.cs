using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// array with a bad start/length combination always throws" theme as InvalidBitConverterArgumentAnalyzer
// (RPAD0268): `new string(char[] value, int startIndex, int length)` throws
// ArgumentOutOfRangeException when startIndex or length is negative, or when `startIndex + length`
// exceeds the array's length — confirmed empirically before writing this, including the boundary case
// (`startIndex == array.Length` with `length == 0` is valid — an empty result — while `startIndex +
// length > array.Length` for any positive length is not). Unlike RPAD0268, this constructor throws a
// SINGLE exception type for every invalid case, not two different ones. A separate analyzer rather than
// an extension of SubstringOutOfRangeAnalyzer (RPAD0235, `"literal".Substring(startIndex, length)`) —
// same underlying "start+length exceeds bounds" shape, but a different call shape entirely (a
// constructor with an array ARGUMENT providing the length, vs. an instance method call on a string
// literal RECEIVER whose length is known directly), matching this project's precedent of separating
// same-concept-different-mechanism analyzers. Scoped to a LITERAL array whose length is known at the
// call site, same helper as RPAD0266/RPAD0267/RPAD0268.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringCharArrayConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0269";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "string(char[], startIndex, length) arguments are out of range",
        messageFormat: "'new string(...)' always throws ArgumentOutOfRangeException at runtime — startIndex ({0}) and length ({1}) don't fit within a {2}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The string(char[], int startIndex, int length) constructor throws ArgumentOutOfRangeException when startIndex or length is negative, or startIndex + length exceeds the array's length.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arrayArg }, { Expression: var startIndexArg }, { Expression: var lengthArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.SpecialType != SpecialType.System_String) return;
        if (ctor.Parameters is not [{ Type: IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char } }, _, _])
            return;

        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        if (startIndex >= 0 && length >= 0 && startIndex + length <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), startIndex, length, arrayLength));
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
