using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known contents" theme as EnumerableToDictionaryDuplicateKeyAnalyzer (RPAD0317):
// `sourceArray.Cast<T>()` on a freshly created array literal containing an element whose runtime type
// isn't `T` (and isn't implicitly convertible to it — boxed value types require an EXACT type match for
// `Cast`, unlike a compile-time implicit conversion) throws `InvalidCastException` once enumerated —
// confirmed empirically via scratch `dotnet run` script, not assumed. Deliberately requires the `Cast<T>
// ()` call to be immediately followed by an eager forcing operation (`.ToList()`/`.ToArray()`/
// `.ToHashSet()`/`.Count()`) rather than flagging a bare `.Cast<T>()` call by itself — `Cast<T>()` alone
// returns a LAZY `IEnumerable<T>` that doesn't throw until actually enumerated, so reporting on the bare
// call without a forcing operation would overclaim "always throws" for code that might never enumerate
// it. Scoped to array-literal elements that are themselves literal (constant) expressions, so each
// element's compile-time type is known precisely.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnumerableCastInvalidElementTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0320";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enumerable.Cast<T>() on a literal array with a mismatched element type always throws",
        messageFormat: "'{0}' always throws InvalidCastException at runtime — the array literal contains an element that isn't a '{1}'",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.Cast<T>() throws InvalidCastException when the source contains an element whose runtime type isn't exactly T, provable here because the receiver is a freshly created array literal with known, literal elements.");

    private static readonly ImmutableHashSet<string> ForcingMethods = ImmutableHashSet.Create(
        "ToList", "ToArray", "ToHashSet", "Count");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var outerInvocation = (InvocationExpressionSyntax)context.Node;
        if (outerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var outerName } outerAccess)
            return;
        if (!ForcingMethods.Contains(outerName)) return;
        if (outerInvocation.ArgumentList.Arguments.Count != 0) return;

        if (outerAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Cast", TypeArgumentList.Arguments: [var targetTypeSyntax] } } castAccess,
                ArgumentList.Arguments.Count: 0,
            } castInvocation)
        {
            return;
        }

        var elements = TryGetArrayLiteralElements(castAccess.Expression);
        if (elements is not { Length: > 0 } elementList) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(targetTypeSyntax, ct).Type is not { } targetType) return;

        foreach (var element in elementList)
        {
            if (!context.SemanticModel.GetConstantValue(element, ct).HasValue) return;
            var elementType = context.SemanticModel.GetTypeInfo(element, ct).Type;
            if (elementType is null || SymbolEqualityComparer.Default.Equals(elementType, targetType)) continue;

            if (context.SemanticModel.GetSymbolInfo(castInvocation, ct).Symbol is not IMethodSymbol { Name: "Cast" } castMethod
                || castMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable")
            {
                return;
            }
            if (context.SemanticModel.GetSymbolInfo(outerInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedOuterName }
                || resolvedOuterName != outerName)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, outerInvocation.GetLocation(), outerInvocation.ToString(), targetType.Name));
            return;
        }
    }

    private static ImmutableArray<ExpressionSyntax>? TryGetArrayLiteralElements(ExpressionSyntax expression)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
            return [.. explicitInit.Expressions];
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return [.. implicitInit.Expressions];
        if (expression is CollectionExpressionSyntax collectionExpr)
            return [.. collectionExpr.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression)];
        return null;
    }
}
