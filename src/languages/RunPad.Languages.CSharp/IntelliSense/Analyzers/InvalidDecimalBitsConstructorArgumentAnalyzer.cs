using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят четвёртый проход, batch) — own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidSemaphoreSlimConstructorArgumentAnalyzer (RPAD0319): `new
// decimal(int[] bits)` throws ArgumentException when the array isn't exactly 4 elements, OR when it IS
// 4 elements but the last one (the scale/sign component) doesn't encode a valid scale (0-28) with the
// rest of its unused bits zero — confirmed empirically via scratch `dotnet run` script that hand-deriving
// the exact bit layout isn't necessary: constructing the REAL `decimal` inside a try/catch on the
// literal int array elements correctly reproduces every rule at once (wrong length, invalid scale byte,
// stray bits), same reasoning as this catalog's other "run the real validator" analyzers — a pure
// managed struct construction, no side effects, safe to probe live during analysis. Scoped to an array
// LITERAL whose elements are themselves literal (constant) `int`s.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDecimalBitsConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0340";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "decimal(int[] bits) constructor argument is invalid",
        messageFormat: "'new decimal(...)' always throws ArgumentException at runtime — the array is not a valid decimal bit representation",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new decimal(int[] bits) throws ArgumentException when the array isn't exactly 4 elements, or when the scale/sign component doesn't encode a valid decimal.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arrayArg }] }) return;

        var elements = TryGetIntArrayLiteralElements(arrayArg);
        if (elements is not { } elementList) return;

        var ct = context.CancellationToken;
        var bits = new int[elementList.Length];
        for (var i = 0; i < elementList.Length; i++)
        {
            if (context.SemanticModel.GetConstantValue(elementList[i], ct).Value is not int value) return;
            bits[i] = value;
        }
        if (!AlwaysThrows(bits)) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.SpecialType != SpecialType.System_Decimal) return;
        if (ctor.Parameters is not [{ Type: IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Int32 } }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static ImmutableArray<ExpressionSyntax>? TryGetIntArrayLiteralElements(ExpressionSyntax expression)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
            return [.. explicitInit.Expressions];
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return [.. implicitInit.Expressions];
        if (expression is CollectionExpressionSyntax collectionExpr)
            return [.. collectionExpr.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression)];
        return null;
    }

    private static bool AlwaysThrows(int[] bits)
    {
        try
        {
            _ = new decimal(bits);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
