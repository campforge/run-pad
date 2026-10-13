using System.Collections.Immutable;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Numerics.Vector<T>` — the hardware-SIMD-width
// generic type, distinct from the already-covered `Vector2`/`Vector3`/`Vector4`/`Quaternion`/
// `Matrix3x2`/`Matrix4x4` (RPAD0370, fixed per-type bounds) and from `Vector128/256/512` (confirmed
// dead end in earlier rounds). Two call shapes, bundled under one diagnostic (precedent RPAD0426):
// `new Vector<T>(T[] values, int index)` — negative `index` throws `ArgumentOutOfRangeException`
// (param `index`, populated); the `this[int index]` indexer — negative index ALSO throws
// `ArgumentOutOfRangeException`, but here `ParamName` is confirmed NULL (a generic message with no
// parameter name), unlike the constructor — the message text intentionally doesn't reference a
// parameter name for the indexer case. DELIBERATELY SCOPED to the negative bound only — the UPPER
// bound depends on `Vector<T>.Count`, which is hardware-SIMD-width-dependent (confirmed 8 for `int` on
// this machine, but varies by CPU/ISA across machines/platforms) — a literal like `v[8]` is NOT
// unconditionally-throwing across all hardware, so this analyzer never flags a "too-large" index,
// consistent with this catalog's schema/state-dependence-avoidance discipline. No
// `[SupportedOSPlatform]` gating, no side effects — pure value-type construction/indexing.
// Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidVectorOfTArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0513";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Vector<T> index is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — the index must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Vector<T>(T[], int index) and Vector<T>'s this[int] indexer throw ArgumentOutOfRangeException for a negative index.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeIndexer, SyntaxKind.ElementAccessExpression);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type: IArrayTypeSymbol }, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Numerics.Vector<T>") return;
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var indexArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (index >= 0) return;

        try
        {
            _ = new Vector<int>(new int[8], index);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString()));
        }
    }

    private static void AnalyzeIndexer(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (index >= 0) return;

        var receiverType = context.SemanticModel.GetTypeInfo(elementAccess.Expression, ct).Type;
        if (receiverType?.OriginalDefinition.ToDisplayString() != "System.Numerics.Vector<T>") return;

        try
        {
            var v = new Vector<int>(new int[8], 0);
            _ = v[index];
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString()));
        }
    }
}
