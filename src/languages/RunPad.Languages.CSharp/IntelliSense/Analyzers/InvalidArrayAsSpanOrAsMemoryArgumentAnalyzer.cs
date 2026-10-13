using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal array with a bad start/length combination always throws" theme as
// InvalidStringCharArrayConstructorArgumentAnalyzer/InvalidEncodingGetStringArgumentAnalyzer
// (RPAD0269/RPAD0270, проходы 280-281): `array.AsSpan(start[, length])` and `array.AsMemory(start[,
// length])` throw ArgumentOutOfRangeException when `start` is negative or beyond the array's length, or
// when a supplied `length` is negative or `start + length` exceeds the array's length (confirmed
// empirically before writing this, including the 1-arg overload where `length` defaults to
// `array.Length - start`, and the boundary case `AsSpan(array.Length, 0)` which is itself invalid since
// the start position alone already exceeds the array). Scoped to a LITERAL array whose length is known
// at the call site, same helper as RPAD0266-0270.
// Extended (триста пятидесятый проход, batch) to also cover the `new Span<T>(array, start, length)`/
// `new Memory<T>(array, start, length)`/`new ReadOnlySpan<T>(array, start, length)`/
// `new ReadOnlyMemory<T>(array, start, length)` constructors — confirmed empirically to follow the
// EXACT same boundary formula and exception type (ArgumentOutOfRangeException) as the `AsSpan`/
// `AsMemory` extension methods, just spelled as an object-creation expression instead of a method
// call, so this analyzer now also registers on `ObjectCreationExpression`/
// `ImplicitObjectCreationExpression` alongside its existing `InvocationExpression` registration.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0271";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AsSpan/AsMemory start/length arguments are out of range",
        messageFormat: "'{0}(...)' always throws ArgumentOutOfRangeException at runtime — start ({1}) and length ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "array.AsSpan(start[, length]), array.AsMemory(start[, length]), and the equivalent Span<T>/Memory<T>/ReadOnlySpan<T>/ReadOnlyMemory<T> constructors throw ArgumentOutOfRangeException when start or length is negative, or start + length exceeds the array's length.");

    private static readonly ImmutableHashSet<string> TargetConstructorTypeNames = ImmutableHashSet.Create(
        "System.Span<T>", "System.Memory<T>", "System.ReadOnlySpan<T>", "System.ReadOnlyMemory<T>");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arrayArg }, { Expression: var startArg }, { Expression: var lengthArg }] })
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startArg, ct).Value is not int start) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;
        if (start >= 0 && length >= 0 && start + length <= arrayLength) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        var typeName = ctor.ContainingType?.OriginalDefinition.ToDisplayString();
        if (typeName is null || !TargetConstructorTypeNames.Contains(typeName)) return;
        if (ctor.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), $"new {ctor.ContainingType!.Name}", start, length, arrayLength));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "AsSpan" or "AsMemory" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not (1 or 2)) return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(access.Expression, context.SemanticModel, ct);
        if (arrayLength is null) return;

        bool isValid;
        int start;
        int? length = null;
        if (arguments.Count == 1)
        {
            if (context.SemanticModel.GetConstantValue(arguments[0].Expression, ct).Value is not int s) return;
            start = s;
            isValid = start >= 0 && start <= arrayLength;
        }
        else
        {
            if (context.SemanticModel.GetConstantValue(arguments[0].Expression, ct).Value is not int s) return;
            if (context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value is not int l) return;
            start = s;
            length = l;
            isValid = start >= 0 && l >= 0 && start + l <= arrayLength;
        }
        if (isValid) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.MemoryExtensions") return;
        // Parameters on a reduced extension-method symbol exclude the receiver, so this checks only the
        // explicit start/length arguments — an Index/Range overload would fail this all-int check.
        if (!method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, start, length?.ToString() ?? "(implicit)", arrayLength));
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
